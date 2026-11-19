using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RecBox.Core.Coaching;

// "Translate to": Off, English (whisper's own translation, offline, as before), or another language translated as text
// by the AI picked for the coach (an AI tool on this PC, or a local model server). The original line always stays; the
// translation goes beside it. Translation never blocks live text or the recording: one call at a time on a worker, a
// failed call backs off like the coach (30 s doubling to 10 min), Stop and exit kill a call in flight.

public static class TranslateTargets
{
    /// <summary>"" is Off. English uses whisper; every other one uses the picked AI.</summary>
    public static readonly IReadOnlyList<string> All = new[] { "", "en", "vi", "ja", "zh", "ko", "fr", "de", "es" };

    public static string Normalize(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        return All.Contains(c) ? c : "";
    }

    public static bool IsOff(string? code) => Normalize(code).Length == 0;
    public static bool IsEnglish(string? code) => Normalize(code) == "en";

    /// <summary>True when the text goes through the picked AI (any target but Off and English).</summary>
    public static bool NeedsAi(string? code) => Normalize(code) is { Length: > 0 } c && c != "en";

    /// <summary>The name on screen, in the interface language.</summary>
    public static string Display(string? code) => IsOff(code) ? Loc.T("translate_off") : Loc.T("lang_" + Normalize(code));

    /// <summary>The English name, for the prompt and the files.</summary>
    public static string EnglishName(string code) => CoachLanguages.Name(Normalize(code));
}

/// <summary>
/// Translate to as the main screen holds it: the saved default every recording starts with, and this recording's pick
/// (one click on the main screen, never saved). After a recording the pick goes back to the default.
/// </summary>
public sealed class TranslateChoice
{
    public TranslateChoice(string? saved)
    {
        Default = TranslateTargets.Normalize(saved);
        Current = Default;
    }

    public string Default { get; private set; }
    public string Current { get; private set; }
    public bool Overridden => Current != Default;
    /// <summary>Whisper itself translates (English): the transcript's mode and live text's translate flag.</summary>
    public bool WhisperTranslates => TranslateTargets.IsEnglish(Current);

    /// <summary>The main screen picker: this recording only.</summary>
    public void Pick(string? code) => Current = TranslateTargets.Normalize(code);

    /// <summary>Settings: a new default; a recording that did not pick its own follows it.</summary>
    public void SetDefault(string? code)
    {
        var following = !Overridden;
        Default = TranslateTargets.Normalize(code);
        if (following) Current = Default;
    }

    public void RecordingEnded() => Current = Default;
}

/// <summary>One batch of lines in, one translation per line out, through any <see cref="ICoachProvider"/>.</summary>
public static class TextTranslator
{
    private static readonly Regex Numbered = new(@"^\s*(\d+)\s*[|:.)]\s*(.*)$", RegexOptions.Compiled);

    public static string System(string to)
    {
        var name = TranslateTargets.EnglishName(to);
        return $"You translate lines of a meeting transcript into {name}. The lines come from automatic speech recognition: "
               + "keep names, numbers and technical terms as they are and do not add or drop anything. "
               + "Answer with exactly one line per input line, in the same order, as `<number>| <translation>`, and nothing else. "
               + $"A line already in {name}: copy it as it is.";
    }

    /// <summary>The instruction's size, for the token estimate.</summary>
    public static int InstructionChars => System("vi").Length;

    public static string User(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++) sb.Append(i + 1).Append("| ").AppendLine(Regex.Replace(lines[i], @"\s+", " ").Trim());
        return sb.ToString();
    }

    /// <summary>The translations by line (null where the reply had none for a line).</summary>
    public static string?[] Parse(string reply, int count)
    {
        var result = new string?[count];
        foreach (var raw in reply.Split('\n'))
        {
            var m = Numbered.Match(raw.Trim().Trim('`'));
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > count) continue;
            var t = m.Groups[2].Value.Trim();
            if (t.Length > 0) result[n - 1] = t;
        }
        return result;
    }

    public static async Task<(string?[] Texts, CoachReply Reply)> TranslateAsync(ICoachProvider p, string to, IReadOnlyList<string> lines, CancellationToken ct)
    {
        var reply = await p.CompleteAsync(System(to), User(lines), ct).ConfigureAwait(false);
        var texts = Parse(reply.Text, lines.Count);
        if (texts.All(t => t is null)) throw new CoachProviderException(Loc.T("translate_empty_reply"));
        return (texts, reply);
    }
}

/// <summary>Calls, tokens and the last problem of a translator, for the screen and session.json.</summary>
public sealed class TranslationStats
{
    public int Calls { get; set; }
    public int Failures { get; set; }
    public int Lines { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public bool TokensEstimated { get; set; }
    public string? LastError { get; set; }

    public void Add(CoachReply r, int lines)
    {
        Calls++;
        Lines += lines;
        InputTokens += r.InputTokens + r.CacheReadTokens + r.CacheWriteTokens;
        OutputTokens += r.OutputTokens;
        if (r.Estimated) TokensEstimated = true;
    }
}

/// <summary>
/// Live lines translated as they come. <see cref="Post"/> never blocks (a lock and a list add); a worker sends up to
/// <see cref="BatchSize"/> lines per call, one call at a time. A failed call backs off 30 s, doubling to 10 min, and its
/// lines are tried once more; at most 200 lines wait (the oldest are dropped: the transcript translation covers them).
/// </summary>
public sealed class LiveTranslator : IDisposable
{
    public const int MaxQueue = 200;
    private readonly ICoachProvider _provider;
    private readonly object _lock = new();
    private readonly List<(string Key, string Text, int Tries)> _queue = new();
    private readonly Dictionary<string, string> _done = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _life = new();
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _callTimeout;
    private readonly Task _loop;
    private CancellationTokenSource? _call;
    private DateTime _backoffUntil = DateTime.MinValue;
    private int _failStreak;
    private bool _stopped;
    private bool _busy;

    public LiveTranslator(ICoachProvider provider, string to, int batchSize = 8, TimeSpan? callTimeout = null, Func<DateTime>? now = null)
    {
        _provider = provider;
        To = TranslateTargets.Normalize(to);
        BatchSize = Math.Max(1, batchSize);
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(90);
        _now = now ?? (() => DateTime.UtcNow);
        _loop = Task.Run(LoopAsync);
    }

    public string To { get; }
    public int BatchSize { get; }
    public ICoachProvider Provider => _provider;
    public TranslationStats Stats { get; } = new();

    /// <summary>Raised on a worker thread: the line's key and its translation.</summary>
    public event Action<string, string>? Translated;

    /// <summary>Raised on a worker thread when a call failed or recovered (the status changed).</summary>
    public event Action? Changed;

    /// <summary>The status for the screen: empty when fine, else why translation waits.</summary>
    public string Status { get; private set; } = "";

    /// <summary>The key of a live line: its start, speaker and text (a line is never changed once shown).</summary>
    public static string Key(TimeSpan start, string speaker, string text) => $"{start.Ticks}|{speaker}|{text}";

    public int Pending
    {
        get { lock (_lock) return _queue.Count; }
    }

    public IReadOnlyDictionary<string, string> Results
    {
        get { lock (_lock) return new Dictionary<string, string>(_done, StringComparer.Ordinal); }
    }

    public string? Get(string key)
    {
        lock (_lock) return _done.TryGetValue(key, out var t) ? t : null;
    }

    /// <summary>Queues a line; a line already in the target language is left as it is.</summary>
    public void Post(string key, string text, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (language is { Length: > 0 } l && l.Split('+')[0] == To) return;
        lock (_lock)
        {
            if (_stopped || _done.ContainsKey(key)) return;
            _queue.Add((key, text, 0));
            if (_queue.Count > MaxQueue) _queue.RemoveRange(0, _queue.Count - MaxQueue);
        }
        _signal.Release();
    }

    private async Task LoopAsync()
    {
        var token = _life.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            List<(string Key, string Text, int Tries)> batch;
            CancellationTokenSource cts;
            lock (_lock)
            {
                if (_queue.Count == 0) continue;
                var wait = _backoffUntil - _now();
                if (wait > TimeSpan.Zero) continue;
                batch = _queue.Take(BatchSize).ToList();
                _queue.RemoveRange(0, batch.Count);
                cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(_callTimeout);
                _call = cts;
                _busy = true;
            }
            await RunAsync(batch, cts).ConfigureAwait(false);
            lock (_lock) if (_queue.Count > 0) _signal.Release();
        }
    }

    private async Task RunAsync(List<(string Key, string Text, int Tries)> batch, CancellationTokenSource cts)
    {
        try
        {
            var (texts, reply) = await TextTranslator.TranslateAsync(_provider, To, batch.Select(b => b.Text).ToList(), cts.Token).ConfigureAwait(false);
            var got = new List<(string, string)>();
            lock (_lock)
            {
                Stats.Add(reply, texts.Count(t => t is not null));
                _failStreak = 0;
                _backoffUntil = DateTime.MinValue;
                for (var i = 0; i < batch.Count; i++)
                    if (texts[i] is { } t)
                    {
                        _done[batch[i].Key] = t;
                        got.Add((batch[i].Key, t));
                    }
                var was = Status;
                Status = "";
                if (was.Length > 0) got.Add(("", ""));
            }
            foreach (var (k, t) in got)
            {
                if (k.Length == 0)
                {
                    RaiseChanged();
                    continue;
                }
                try { Translated?.Invoke(k, t); }
                catch (Exception ex) { Log.Error("translation handler", ex); }
            }
        }
        catch (Exception ex)
        {
            if (_life.IsCancellationRequested) return; // Stop or exit ended it: not a failure
            var why = ex is OperationCanceledException
                ? Loc.F("coach_no_answer", _callTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture))
                : CoachProviders.Safe(ex.Message, null);
            lock (_lock)
            {
                _failStreak++;
                Stats.Failures++;
                Stats.LastError = why;
                var wait = Math.Min(600, 30 * Math.Pow(2, _failStreak - 1));
                _backoffUntil = _now() + TimeSpan.FromSeconds(wait);
                // tried once more after the backoff, ahead of newer lines
                _queue.InsertRange(0, batch.Where(b => b.Tries < 1).Select(b => (b.Key, b.Text, b.Tries + 1)));
                if (_queue.Count > MaxQueue) _queue.RemoveRange(MaxQueue, _queue.Count - MaxQueue);
                Status = Loc.F("translate_retry", why, wait.ToString("0", CultureInfo.InvariantCulture));
            }
            Log.Warn($"translation call failed ({_provider.Name}): {why}");
            RaiseChanged();
        }
        finally
        {
            lock (_lock)
            {
                _busy = false;
                _call = null;
            }
            cts.Dispose();
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("translation changed handler", ex); }
    }

    /// <summary>Stop: lines still waiting get up to the grace to be translated (no backoff wait); then a call in flight is killed.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        lock (_lock)
        {
            if (_stopped) return;
            _stopped = true;
            _backoffUntil = DateTime.MinValue;
            _failStreak = 0;
        }
        _signal.Release();
        var until = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < until)
        {
            lock (_lock)
                if (_queue.Count == 0 && !_busy) break;
            await Task.Delay(50).ConfigureAwait(false);
        }
        End();
        await Task.WhenAny(_loop, Task.Delay(CoachEngine.EndWait)).ConfigureAwait(false);
    }

    private void End()
    {
        lock (_lock)
        {
            _stopped = true;
            _queue.Clear();
        }
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        End();
        try { _loop.Wait(CoachEngine.EndWait); }
        catch (Exception) { }
    }
}

/// <summary>
/// The saved transcript translated: transcript.&lt;lang&gt;.md beside transcript.md, each line's translation with the original
/// under it. Lines live text already translated are reused (same text), the rest go in batches of 12.
/// </summary>
public static class TranscriptTranslator
{
    public static readonly Regex LinePattern = new(@"^\[(\S+)\] \*\*(.+?):\*\* (.*)$", RegexOptions.Compiled);

    public static string FileName(string to) => $"transcript.{TranslateTargets.Normalize(to)}.md";

    public sealed record Result(string? File, int Lines, int Translated, TranslationStats Stats, string? Error);

    public static async Task<Result> TranslateAsync(ICoachProvider p, string to, string dir, IReadOnlyDictionary<string, string>? known,
        Action<int, int>? progress, CancellationToken ct, int batchSize = 12)
    {
        to = TranslateTargets.Normalize(to);
        var stats = new TranslationStats();
        var src = Path.Combine(dir, "transcript.md");
        if (!File.Exists(src)) return new Result(null, 0, 0, stats, Loc.T("translate_no_transcript"));
        var lines = await File.ReadAllLinesAsync(src, ct).ConfigureAwait(false);
        var items = lines.Select((l, i) => (i, m: LinePattern.Match(l))).Where(x => x.m.Success).ToList();
        var outText = new string?[lines.Length];
        var todo = new List<(int Index, string Text)>();
        foreach (var (i, m) in items)
        {
            var text = m.Groups[3].Value;
            if (known is not null && known.TryGetValue(text, out var t)) outText[i] = t;
            else todo.Add((i, text));
        }
        string? error = null;
        var done = 0;
        for (var b = 0; b < todo.Count; b += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = todo.Skip(b).Take(batchSize).ToList();
            string?[]? texts = null;
            for (var attempt = 0; attempt < 2 && texts is null; attempt++)
            {
                try
                {
                    var (t, reply) = await TextTranslator.TranslateAsync(p, to, batch.Select(x => x.Text).ToList(), ct).ConfigureAwait(false);
                    stats.Add(reply, t.Count(x => x is not null));
                    texts = t;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    stats.Failures++;
                    error = CoachProviders.Safe(ex.Message, null);
                    Log.Warn($"transcript translation call failed ({p.Name}): {error}");
                    if (attempt == 0) await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
            }
            if (texts is null) break; // the rest stays untranslated; the file says so
            error = null;
            for (var k = 0; k < batch.Count; k++) outText[batch[k].Index] = texts[k];
            done += batch.Count;
            progress?.Invoke(Math.Min(items.Count, done + (items.Count - todo.Count)), items.Count);
        }

        var sb = new StringBuilder();
        var name = TranslateTargets.EnglishName(to);
        sb.AppendLine($"# Transcript in {name}");
        sb.AppendLine();
        sb.AppendLine($"Translated by {p.Name} from transcript.md (automatic speech recognition, then machine translation: check names and figures). The original line is under each translation.");
        if (error is not null) sb.AppendLine($"Translation stopped early ({error}); the lines after that are the original only.");
        sb.AppendLine();
        var translated = 0;
        foreach (var (i, m) in items)
        {
            if (outText[i] is { } t)
            {
                translated++;
                sb.AppendLine($"[{m.Groups[1].Value}] **{m.Groups[2].Value}:** {t}");
                sb.AppendLine($"> {m.Groups[3].Value}");
            }
            else sb.AppendLine(lines[i]);
            sb.AppendLine();
        }
        var file = FileName(to);
        await File.WriteAllTextAsync(Path.Combine(dir, file), sb.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        return new Result(file, items.Count, translated, stats, error);
    }

    /// <summary>The translation's entry and file in session.json.</summary>
    public static void Record(string dir, Session.SessionManifest manifest, Session.TranslationInfo info)
    {
        lock (manifest)
        {
            manifest.Translation = info;
            if (info.File is { } f)
            {
                manifest.Files.RemoveAll(x => x.Name == f);
                manifest.Files.Add(new Session.FileEntry
                {
                    Name = f,
                    Description = $"the transcript translated to {TranslateTargets.EnglishName(info.To)} by {info.Provider}, the original line under each translation",
                });
            }
        }
        Transcription.TranscriptionService.SaveManifest(dir, manifest);
    }
}
