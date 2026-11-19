using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RecBox.Core.Transcription;

// ---------------------------------------------------------------------------------------------
// What makes the text good, for live text and the transcript after Stop alike (the owner's live
// meeting kit, ported): the right graphics card, one language per session, Silero speech
// segments, confidence gates, a hallucination blocklist, a script filter, loop collapse and an
// echo tag for the mic hearing the speakers.
// ---------------------------------------------------------------------------------------------

// ================= graphics card =================

public enum VulkanDeviceType
{
    Other = 0,
    Integrated = 1,
    Discrete = 2,
    Virtual = 3,
    Cpu = 4,
}

/// <summary>One Vulkan physical device, in Vulkan's order (the order ggml numbers them: Vulkan0, Vulkan1, ...).</summary>
public sealed record VulkanDevice(int Index, string Name, VulkanDeviceType Type, long LocalBytes)
{
    public string Describe() =>
        $"Vulkan{Index} = {Name} ({Type.ToString().ToLowerInvariant()}, {SystemInfo.Gb(LocalBytes)} own memory)";
}

/// <summary>
/// Which graphics card whisper.cpp runs on. Vulkan lists devices in driver order, not by speed: a laptop lists
/// its Intel graphics first, so "device 0" put the 2026-11-19 meeting on Iris Xe instead of the RTX 3060.
/// The discrete card with the most memory of its own wins; integrated graphics only when there is no other.
/// </summary>
public static class GpuDevicePick
{
    public static VulkanDevice? Pick(IReadOnlyList<VulkanDevice> devices) =>
        devices.Where(d => d.Type == VulkanDeviceType.Discrete).OrderByDescending(d => d.LocalBytes).ThenBy(d => d.Index).FirstOrDefault()
        ?? devices.Where(d => d.Type is VulkanDeviceType.Integrated or VulkanDeviceType.Virtual)
            .OrderByDescending(d => d.LocalBytes).ThenBy(d => d.Index).FirstOrDefault();
}

/// <summary>Lists Vulkan devices through vulkan-1.dll (empty when there is no Vulkan driver).</summary>
public static class VulkanDevices
{
    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public int SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr AppInfo;
        public uint LayerCount;
        public IntPtr Layers;
        public uint ExtensionCount;
        public IntPtr Extensions;
    }

    [DllImport("vulkan-1.dll")]
    private static extern int vkCreateInstance(ref InstanceCreateInfo info, IntPtr allocator, out IntPtr instance);

    [DllImport("vulkan-1.dll")]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    [DllImport("vulkan-1.dll")]
    private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, [Out] IntPtr[]? devices);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceMemoryProperties(IntPtr device, IntPtr properties);

    public static IReadOnlyList<VulkanDevice> List()
    {
        var list = new List<VulkanDevice>();
        try
        {
            var info = new InstanceCreateInfo { SType = 1 }; // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
            if (vkCreateInstance(ref info, IntPtr.Zero, out var instance) != 0 || instance == IntPtr.Zero) return list;
            var props = Marshal.AllocHGlobal(4096);
            var mem = Marshal.AllocHGlobal(1024);
            try
            {
                uint count = 0;
                if (vkEnumeratePhysicalDevices(instance, ref count, null) != 0 || count == 0) return list;
                var devices = new IntPtr[count];
                if (vkEnumeratePhysicalDevices(instance, ref count, devices) < 0) return list;
                for (var i = 0; i < count; i++)
                {
                    vkGetPhysicalDeviceProperties(devices[i], props);
                    var type = (VulkanDeviceType)Marshal.ReadInt32(props, 16);
                    var name = Marshal.PtrToStringUTF8(props + 20) ?? $"device {i}";
                    vkGetPhysicalDeviceMemoryProperties(devices[i], mem);
                    var heaps = Marshal.ReadInt32(mem, 260);
                    long local = 0;
                    for (var h = 0; h < heaps && h < 16; h++)
                    {
                        var size = Marshal.ReadInt64(mem, 264 + h * 16);
                        var flags = Marshal.ReadInt32(mem, 264 + h * 16 + 8);
                        if ((flags & 1) != 0) local += size; // VK_MEMORY_HEAP_DEVICE_LOCAL_BIT
                    }
                    list.Add(new VulkanDevice(i, name.Trim(), type, local));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(props);
                Marshal.FreeHGlobal(mem);
                vkDestroyInstance(instance, IntPtr.Zero);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Log.Info($"no Vulkan loader: {ex.Message}");
        }
        return list;
    }
}

// ================= language =================

/// <summary>
/// The spoken-language setting: "auto" (detect once on the first speech, then keep it for the session), one
/// language ("vi", "en", ...) for every segment, or a mixed pair ("vi+en": Vietnamese unless Whisper is sure
/// a segment is English, p >= 0.8). Never free detection per chunk: that made 164 of 284 chunks of a
/// Vietnamese meeting come out in other languages.
/// </summary>
public static class Languages
{
    public const string Auto = "auto";
    public const string ViEn = "vi+en";
    public const float AdaptProbability = 0.8f;

    public static readonly IReadOnlyList<(string Code, string Name)> Choices = new[]
    {
        (Auto, "Auto (detect, then keep)"),
        ("vi", "Vietnamese"),
        ("en", "English"),
        (ViEn, "Vietnamese + English"),
        ("vi,ja", "Vietnamese or Japanese"),
        ("vi,en,ja", "Vietnamese, English or Japanese"),
        ("ja", "Japanese"),
        ("ko", "Korean"),
        ("zh", "Chinese"),
        ("fr", "French"),
        ("de", "German"),
        ("es", "Spanish"),
    };

    private static readonly Dictionary<string, string> Names = new()
    {
        ["vi"] = "Vietnamese", ["en"] = "English", ["ja"] = "Japanese", ["ko"] = "Korean", ["zh"] = "Chinese", ["fr"] = "French",
        ["de"] = "German", ["es"] = "Spanish", ["ru"] = "Russian", ["th"] = "Thai", ["id"] = "Indonesian", ["it"] = "Italian",
        ["pt"] = "Portuguese", ["nl"] = "Dutch", ["pl"] = "Polish", ["tr"] = "Turkish", ["ar"] = "Arabic", ["hi"] = "Hindi",
        ["uk"] = "Ukrainian", ["ms"] = "Malay", ["tl"] = "Tagalog", ["km"] = "Khmer", ["lo"] = "Lao", ["yue"] = "Cantonese",
    };

    public static bool IsAuto(string? spec) => string.IsNullOrWhiteSpace(spec) || spec == Auto;

    /// <summary>"vi+en" (the first language, another only when sure) or an allowed set "vi,ja" (each piece in whichever it is).</summary>
    public static bool IsMixed(string? spec) => spec?.Contains('+') == true || spec?.Contains(',') == true;

    /// <summary>The kit's allowed set ("vi,ja", set-lang.ps1): each piece is decoded in whichever of the set it is.</summary>
    public static bool IsAnyOf(string? spec) => spec?.Contains(',') == true;

    private static readonly char[] SetSeparators = { '+', ',' };

    public static string Name(string? spec)
    {
        if (IsAuto(spec)) return "Auto";
        if (IsAnyOf(spec))
        {
            var p = spec!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Name).ToArray();
            return p.Length < 2 ? string.Join("", p) : string.Join(", ", p[..^1]) + " or " + p[^1];
        }
        if (IsMixed(spec)) return string.Join(" + ", spec!.Split('+').Select(Name));
        return Names.TryGetValue(spec!, out var n) ? n : spec!;
    }

    /// <summary>The name on screen, in the interface language (<see cref="Name"/> stays English: files and logs use it).</summary>
    public static string Display(string? spec)
    {
        if (IsAuto(spec)) return Loc.T("lang_auto");
        if (IsAnyOf(spec))
        {
            var p = spec!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Display).ToArray();
            return p.Length < 2 ? string.Join("", p) : Loc.F("lang_or", string.Join(", ", p[..^1]), p[^1]);
        }
        if (IsMixed(spec)) return string.Join(" + ", spec!.Split('+').Select(Display));
        return Loc.Has("lang_" + spec) ? Loc.T("lang_" + spec) : Name(spec);
    }

    /// <summary>A language picker entry: Auto says how it works, the rest as <see cref="Display"/>.</summary>
    public static string ChoiceDisplay(string code) => IsAuto(code) ? Loc.T("lang_auto_choice") : Display(code);

    /// <summary>"vi+en" -> ("vi", ["en"]); "vi,ja" -> ("vi", ["ja"]); a single code -> (code, []).</summary>
    public static (string Primary, string[] Extra) Split(string spec)
    {
        var parts = spec.Split(SetSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? ("en", Array.Empty<string>()) : (parts[0], parts.Skip(1).ToArray());
    }

    /// <summary>
    /// Mixed mode, the kit's rules: "vi+en" keeps the first language unless detection is sure (p >= 0.8) of another one;
    /// "vi,ja" takes whichever of the set was detected, and the first one when detection names a language outside the set.
    /// </summary>
    public static string ChooseMixed(string spec, string? detected, float probability)
    {
        var (primary, extra) = Split(spec);
        if (IsAnyOf(spec)) return detected is not null && (detected == primary || extra.Contains(detected)) ? detected : primary;
        return detected is not null && extra.Contains(detected) && probability >= AdaptProbability ? detected : primary;
    }

    /// <summary>The languages whose scripts a segment may use (empty: any).</summary>
    public static IReadOnlyCollection<string> Allowed(string? spec, string? decoded)
    {
        if (IsMixed(spec)) return spec!.Split(SetSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!IsAuto(spec)) return new[] { spec! };
        return decoded is { Length: > 0 } d && d != Auto ? new[] { d } : Array.Empty<string>();
    }
}

/// <summary>
/// The language one recording decodes with. Auto detects on the first chunk with real speech and keeps it:
/// the first detection with p >= 0.5 on at least 1.5 s of speech, else the most frequent of the first three.
/// A change (the language picker during a recording) applies from the next chunk.
/// </summary>
public sealed class LanguageLock
{
    /// <summary>Speech before the vote leader is used at all.</summary>
    public const double MinSpeechSeconds = 1.5;
    /// <summary>Speech the vote runs over before the language is locked for the session (eval: wrong detections had p &gt; 0.8).</summary>
    public const double VoteSeconds = 60;

    private readonly Dictionary<string, double> _votes = new();
    private readonly object _gate = new();
    private double _speech;

    public LanguageLock(string? setting) => Setting = Normalize(setting);

    public string Setting { get; private set; }

    /// <summary>What Auto locked after the vote (null until then, and for a fixed setting).</summary>
    public string? Detected { get; private set; }

    /// <summary>The vote leader so far (Auto, before the lock): what chunks are decoded in meanwhile.</summary>
    public string? Leading
    {
        get { lock (_gate) return LeaderLocked(); }
    }

    public bool IsLocked
    {
        get { lock (_gate) return !Languages.IsAuto(Setting) || Detected is not null; }
    }

    /// <summary>True while Auto still votes: the worker detects on each chunk (only to vote; it decodes in <see cref="Next"/>).</summary>
    public bool NeedsDetection
    {
        get { lock (_gate) return Languages.IsAuto(Setting) && Detected is null; }
    }

    /// <summary>The language the next chunk is decoded in: the setting, the locked or leading language, or "auto" before any speech.</summary>
    public string Next
    {
        get { lock (_gate) return Languages.IsAuto(Setting) ? Detected ?? LeaderLocked() ?? Languages.Auto : Setting; }
    }

    /// <summary>What a transcript of this session should use: the fixed setting, the detected (or leading) language, or "auto".</summary>
    public string Effective => Next;

    /// <summary>Share of the votes the leader needs to lock at <see cref="VoteSeconds"/>; without it the vote runs to twice that.</summary>
    public const double ClearWinner = 0.6;

    /// <summary>
    /// A vote from one chunk: its detected language, weighed by probability and seconds of speech (<paramref name="weight"/>
    /// lowers the microphone's say: the eval votes on the other side's speech); true when this locked it.
    /// </summary>
    public bool Offer(string? language, float probability, double speechSeconds, double weight = 1.0)
    {
        lock (_gate)
        {
            if (!Languages.IsAuto(Setting) || Detected is not null || string.IsNullOrEmpty(language) || language == Languages.Auto) return false;
            var secs = Math.Max(0, speechSeconds);
            _votes[language] = _votes.GetValueOrDefault(language) + Math.Clamp(probability, 0f, 1f) * secs * Math.Clamp(weight, 0, 1);
            _speech += secs;
            if (_speech < VoteSeconds) return false;
            var sum = _votes.Values.Sum();
            var lead = LeaderLocked();
            if (lead is null) return false;
            if (sum > 0 && _votes[lead] / sum < ClearWinner && _speech < 2 * VoteSeconds) return false;
            Detected = lead;
            return true;
        }
    }

    public void Change(string? setting)
    {
        lock (_gate)
        {
            Setting = Normalize(setting);
            Detected = null;
            _votes.Clear();
            _speech = 0;
        }
    }

    /// <summary>For the screen: "Vietnamese (detected)", "Vietnamese (detecting)", "Auto: listening for the language".</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (!Languages.IsAuto(Setting)) return Languages.Display(Setting);
            if (Detected is { } d) return Loc.F("lang_detected", Languages.Display(d));
            return LeaderLocked() is { } l ? Loc.F("lang_detecting", Languages.Display(l)) : Loc.T("lang_listening");
        }
    }

    private string? LeaderLocked() =>
        _speech >= MinSpeechSeconds && _votes.Count > 0 ? _votes.OrderByDescending(v => v.Value).First().Key : null;

    private static string Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? Languages.Auto : s.Trim().ToLowerInvariant();
}

// ================= filters =================

public enum Confidence
{
    Keep,
    Unsure,
    Drop,
}

/// <summary>One decoded piece of text and whether it is low confidence (shown with "[?]").</summary>
public sealed record Piece(string Text, bool Low);

/// <summary>
/// The kit's filters (live.py _pieces, join_pieces, collapse_loops, hallucinated, script_ok): what Whisper says
/// on silence, noise and music never reaches the text; what it is unsure of is tagged "[?]", never removed.
/// </summary>
public static class TranscriptFilter
{
    public const double DropNoSpeech = 0.9;
    public const double UnsureNoSpeech = 0.6;
    public const double LowLogprob = -1.0;

    public static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Outro and subtitle-credit hallucinations (the kit's BAD list, plus "see you next time"), matched on folded text.</summary>
    private static readonly string[] Bad =
    {
        "dang ky kenh", "dang ki kenh", "theo doi va hen gap lai", "cam on cac ban da theo doi", "hay dang ky", "hay subscribe",
        "video hap dan", "lalaschool", "ghien mi go", "hen gap lai cac ban trong nhung video", "video tiep theo",
        "subscribe to my channel", "subscribe to the channel", "subscribe to our channel", "like and subscribe", "please subscribe",
        "thanks for watching", "thank you for watching", "amara.org", "subtitles by", "sous-titres realises par", "sous-titrage",
        "subtitulos realizados por", "subtitulado por", "продолжение следует", "подпишись на канал", "подписывайтесь на канал",
        "субтитры сделал", "субтитры подготовил", "редактор субтитров", "구독과 좋아요", "구독 좋아요", "시청해 주셔서 감사합니다",
        "ご視聴ありがとうございました", "チャンネル登録", "最後までご視聴", "字幕由", "请不吝点赞", "订阅我的频道",
        "merci d'avoir regarde", "merci d avoir regarde", "abonnez-vous", "gracias por ver", "suscribete", "tekstitys:",
        "tekstitys lasse", "kiitos kun katsoit", "kiitos katsomisesta", "tekster af", "tak fordi du s", "undertekster af",
        "see you next time", "see you in the next video", "cam on cac ban da xem video", "nho like share",
    };

    private static readonly string[] BadFolded = Bad.Select(Fold).ToArray();

    /// <summary>Whole lines that are an outro on their own ("Bye-bye!"); inside a longer line they are kept.</summary>
    private static readonly string[] BadWhole = { "bye bye", "bye bye bye", "hen gap lai", "hen gap lai cac ban" };

    public static bool Hallucinated(string text)
    {
        var t = Fold(text);
        if (BadFolded.Any(b => t.Contains(b, StringComparison.Ordinal))) return true;
        var whole = Regex.Replace(t, @"[\W_]+", " ").Trim();
        return BadWhole.Contains(whole);
    }

    /// <summary>Whisper's own markers ("[Inaudible]", "(water splashes)", "[Metak]", "*music*", "♪"): never words anyone said.</summary>
    private static readonly Regex TagOnly = new(@"^\s*(?:(?:\[[^\]]*\]|\([^\)]*\)|\*[^\*]*\*|[♪♫]+)[\s.,!?-]*)+$", RegexOptions.CultureInvariant);

    private static readonly Regex InlineTag = new(@"\[(?!\?\])[^\]]{1,40}\]|[♪♫]+", RegexOptions.CultureInvariant);

    public static bool IsTagOnly(string text) => TagOnly.IsMatch(text);

    /// <summary>Empty, a marker, or a known hallucination.</summary>
    public static bool IsNoise(string text) => string.IsNullOrWhiteSpace(text) || IsTagOnly(text) || Hallucinated(text);

    private static readonly (string Name, Regex Rx)[] Scripts =
    {
        ("cyrillic", new Regex(@"[Ѐ-ԯ]")), ("hangul", new Regex(@"[가-힯ᄀ-ᇿ㄰-㆏]")),
        ("kana", new Regex(@"[぀-ヿㇰ-ㇿｦ-ﾟ]")), ("han", new Regex(@"[㐀-䶿一-鿿]")),
        ("arabic", new Regex(@"[؀-ۿ]")), ("thai", new Regex(@"[฀-๿]")), ("hebrew", new Regex(@"[֐-׿]")),
        ("devanagari", new Regex(@"[ऀ-ॿ]")), ("greek", new Regex(@"[Ͱ-Ͽ]")),
    };

    private static readonly Dictionary<string, string[]> LangScripts = new()
    {
        ["ja"] = new[] { "kana", "han" }, ["zh"] = new[] { "han" }, ["yue"] = new[] { "han" }, ["ko"] = new[] { "hangul", "han" },
        ["ru"] = new[] { "cyrillic" }, ["uk"] = new[] { "cyrillic" }, ["bg"] = new[] { "cyrillic" }, ["sr"] = new[] { "cyrillic" },
        ["be"] = new[] { "cyrillic" }, ["mk"] = new[] { "cyrillic" }, ["kk"] = new[] { "cyrillic" }, ["el"] = new[] { "greek" },
        ["ar"] = new[] { "arabic" }, ["fa"] = new[] { "arabic" }, ["ur"] = new[] { "arabic" }, ["th"] = new[] { "thai" },
        ["he"] = new[] { "hebrew" }, ["hi"] = new[] { "devanagari" }, ["mr"] = new[] { "devanagari" }, ["ne"] = new[] { "devanagari" },
    };

    /// <summary>False when over 20 % of the letters are in a script none of the allowed languages writes (Latin always passes).</summary>
    public static bool ScriptOk(string text, IReadOnlyCollection<string> allowed)
    {
        var ok = allowed.Count == 0
            ? Scripts.Select(s => s.Name).ToHashSet()
            : allowed.SelectMany(l => LangScripts.TryGetValue(l, out var s) ? s : Array.Empty<string>()).ToHashSet();
        var letters = text.Count(char.IsLetter);
        if (letters == 0) return true;
        var foreign = Scripts.Where(s => !ok.Contains(s.Name)).Sum(s => s.Rx.Matches(text).Count);
        return foreign <= 0.2 * letters;
    }

    private static readonly Regex LoopWords = new(@"\b(\w+(?:\s+\w+){0,3}?)(?:[\s,.!?;:、。…-]+\1\b){2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LoopCjk = new(@"([぀-ヿ㐀-䶿一-鿿가-힯]{1,8}?)(?:[、。,\s]*\1){2,}", RegexOptions.CultureInvariant);

    /// <summary>A word or phrase (1-4 words, or 1-8 CJK characters) said 3+ times in a row is kept once.</summary>
    public static string CollapseLoops(string text)
    {
        for (var i = 0; i < 3; i++)
        {
            var t = LoopCjk.Replace(LoopWords.Replace(text, "$1"), "$1");
            if (t == text) break;
            text = t;
        }
        return text;
    }

    /// <summary>True when the text holds a word or phrase said 3+ times in a row (what CollapseLoops removes).</summary>
    public static bool HasLoop(string text) => LoopWords.IsMatch(text) || LoopCjk.IsMatch(text);

    public static Confidence Gate(double noSpeech, double avgLogprob)
    {
        if (noSpeech >= DropNoSpeech && avgLogprob <= LowLogprob) return Confidence.Drop;
        if (noSpeech >= UnsureNoSpeech || avgLogprob <= LowLogprob) return Confidence.Unsure;
        return Confidence.Keep;
    }

    /// <summary>One decoded segment through every filter: null when dropped, else the piece (tagged low when unsure).</summary>
    public static Piece? Filter(WorkerSegment s, IReadOnlyCollection<string> allowed)
    {
        var raw = InlineTag.Replace(s.Text ?? "", " ");
        raw = Regex.Replace(raw, @"\s{2,}", " ").Trim();
        var t = CollapseLoops(raw);
        if (t.Length == 0 || IsTagOnly(t) || Hallucinated(t)) return null;
        var gate = Gate(s.NoSpeech, s.AvgLogprob);
        if (gate == Confidence.Drop) return null;
        if (allowed.Count > 0 && !ScriptOk(t, allowed)) return null;
        return new Piece(t, gate == Confidence.Unsure);
    }

    public static List<Piece> Pieces(IEnumerable<WorkerSegment> segments, IReadOnlyCollection<string> allowed) =>
        segments.Select(s => Filter(s, allowed)).Where(p => p is not null).Select(p => p!).ToList();

    private static string Key(string t) => Regex.Replace(t, @"\W+", " ").Trim().ToLowerInvariant();

    /// <summary>
    /// Pieces to one line, "[?] " before a low-confidence piece. A piece said 3+ times in a row across segments is kept
    /// once ("[?] " only when every repeat was low), then the joined line goes through CollapseLoops.
    /// </summary>
    public static string Join(IReadOnlyList<Piece> pieces)
    {
        var runs = new List<(string Key, List<Piece> Items)>();
        foreach (var p in pieces)
        {
            var k = Key(p.Text);
            if (runs.Count > 0 && k.Length > 0 && runs[^1].Key == k) runs[^1].Items.Add(p);
            else runs.Add((k, new List<Piece> { p }));
        }
        var outList = new List<string>();
        foreach (var (_, items) in runs)
        {
            var rs = items.Count >= 3 ? new List<Piece> { new(items[0].Text, items.All(i => i.Low)) } : items;
            outList.AddRange(rs.Select(r => (r.Low ? "[?] " : "") + r.Text));
        }
        return CollapseLoops(string.Join(" ", outList).Trim());
    }

    /// <summary>Lines in a row of one speaker that repeat the same words 3+ times keep only the first (loops across pieces).</summary>
    public static List<T> CollapseLineRuns<T>(IReadOnlyList<T> lines, Func<T, string> text, Func<T, string> speaker)
    {
        var result = new List<T>();
        var runs = new List<(T Line, string Key, string Speaker)>();
        foreach (var l in lines) runs.Add((l, Key(text(l).Replace("[?]", "")), speaker(l)));
        // count each line's run length within its own speaker's sequence
        var keep = new bool[runs.Count];
        var bySpeaker = runs.Select((r, i) => (r, i)).GroupBy(x => x.r.Speaker);
        foreach (var g in bySpeaker)
        {
            var items = g.ToList();
            for (var a = 0; a < items.Count;)
            {
                var b = a;
                while (b + 1 < items.Count && items[b + 1].r.Key.Length > 0 && items[b + 1].r.Key == items[a].r.Key) b++;
                var n = b - a + 1;
                if (n >= 3) keep[items[a].i] = true;
                else for (var k = a; k <= b; k++) keep[items[k].i] = true;
                a = b + 1;
            }
        }
        for (var i = 0; i < runs.Count; i++) if (keep[i]) result.Add(runs[i].Line);
        return result;
    }

    /// <summary>Most times one speaker's line (4+ words) may appear in a whole transcript; more is the model looping.</summary>
    public const int MaxSameLine = 3;

    /// <summary>
    /// The repeat rule shared by live text and the transcript, for one speaker's lines in order: a line with the same
    /// words as the line before (4+ words) or as the two before, or a 4+ word line already said <see cref="MaxSameLine"/>
    /// times, is the model looping. <paramref name="seen"/> carries that speaker's history.
    /// </summary>
    public sealed class RepeatGuard
    {
        /// <summary>A 4+ word line said again by the same source within this many seconds is a repeat (eval I5 d).</summary>
        public const double WithinSeconds = 60;

        private readonly List<string> _last = new();
        private readonly Dictionary<string, int> _count = new();
        private readonly Dictionary<string, double> _at = new();

        public bool IsRepeat(string text, double atSeconds = double.NaN)
        {
            var key = Key(text.Replace("[?]", ""));
            if (key.Length == 0) return false;
            var words = key.Split(' ').Length;
            var repeat = _last.Count > 0 && _last[^1] == key && (words >= 4 || (_last.Count > 1 && _last[^2] == key));
            var n = _count.GetValueOrDefault(key);
            if (words >= 4 && n >= MaxSameLine) repeat = true;
            if (words >= 4 && !double.IsNaN(atSeconds) && _at.TryGetValue(key, out var prev) && atSeconds - prev <= WithinSeconds) repeat = true;
            _last.Add(key);
            if (_last.Count > 2) _last.RemoveAt(0);
            if (!repeat)
            {
                _count[key] = n + 1;
                if (!double.IsNaN(atSeconds)) _at[key] = atSeconds;
            }
            return repeat;
        }
    }

    /// <summary>Drops the lines <see cref="RepeatGuard"/> calls a loop, per speaker.</summary>
    public static List<T> DropRepeats<T>(IReadOnlyList<T> lines, Func<T, string> text, Func<T, string> speaker, Func<T, double>? at = null)
    {
        var guards = new Dictionary<string, RepeatGuard>();
        var result = new List<T>();
        foreach (var l in lines)
        {
            var s = speaker(l);
            if (!guards.TryGetValue(s, out var g)) guards[s] = g = new RepeatGuard();
            if (!g.IsRepeat(text(l), at is null ? double.NaN : at(l))) result.Add(l);
        }
        return result;
    }
}

// ================= echo =================

/// <summary>A line with its time, for the echo test.</summary>
public sealed record TimedText(double Start, double End, string Text);

/// <summary>
/// The mic hearing the speakers (no headset): a mic line is tagged echo, never removed, when speaker lines that
/// overlap it in time AND share its words cover at least half of it (live.py is_echo, flush_pending).
/// </summary>
public static class EchoTag
{
    public const double Overlap = 0.5;
    public const double Window = 3.0;
    public const double Hold = 8.0;

    private static readonly HashSet<string> Stop = new(("the and for with that this from you your are was were have has had not but can will would could should about " +
        "what when where which who how why our out all any one two its it's they them their there then than into also just " +
        "very yes okay well like some more much many such only over each other").Split(' '));

    public static HashSet<string> Tokens(string s)
    {
        var f = TranscriptFilter.Fold(s.Replace("[?]", " "));
        var w = Regex.Matches(f, "[a-z0-9]+").Select(m => m.Value).Where(x => x.Length > 2 && !Stop.Contains(x)).ToHashSet();
        var cj = Regex.Replace(f, @"[^぀-ヿ㐀-䶿一-鿿가-힯ᄀ-ᇿ]", "");
        for (var i = 0; i + 1 < cj.Length; i++) w.Add(cj.Substring(i, 2));
        return w;
    }

    private static string Norm(string x) => Regex.Replace(TranscriptFilter.Fold(x.Replace("[?]", "")), @"\W+", " ").Trim();

    public static bool IsEcho(string me, IReadOnlyCollection<string> remote, double thr = 0.5)
    {
        if (remote.Count == 0) return false;
        var a = Tokens(me);
        if (a.Count < 3)
        {
            var m = Norm(me);
            return m.Length > 0 && remote.Any(r => Norm(r) == m);
        }
        var u = new HashSet<string>();
        foreach (var r in remote) u.UnionWith(Tokens(r));
        return a.Count(u.Contains) / (double)a.Count >= thr;
    }

    /// <summary>True when this mic line is the speakers heard by the mic (the kit's flush_pending rule).</summary>
    public static bool Check(TimedText me, IEnumerable<TimedText> remote)
    {
        var near = remote.Where(r => r.End >= me.Start - Window && r.Start <= me.End + Window).ToList();
        if (near.Count == 0) return false;
        var dur = Math.Max(me.End - me.Start, 0.1);
        var mt = Tokens(me.Text);
        var match = new List<(double Ov, string Text)>();
        foreach (var r in near)
        {
            var ov = Math.Max(0, Math.Min(me.End, r.End + 0.5) - Math.Max(me.Start, r.Start - 0.5));
            var rt = Tokens(r.Text);
            var shared = mt.Count > 0 && rt.Count > 0 && mt.Count(rt.Contains) / (double)Math.Min(mt.Count, rt.Count) >= 0.5;
            if (ov > 0 && (shared || IsEcho(me.Text, new[] { r.Text }))) match.Add((ov, r.Text));
        }
        return match.Count > 0 && Math.Min(match.Sum(m => m.Ov) / dur, 1.0) >= Overlap && IsEcho(me.Text, match.Select(m => m.Text).ToList());
    }
}

// ================= voice activity and the segmenter =================

/// <summary>Speech spans of a buffer, in samples; null when the detector is unavailable (the caller falls back).</summary>
public delegate IReadOnlyList<(int Start, int End)>? SpeechDetector(float[] samples, int count);

/// <summary>Speech spans from loudness alone: the fallback when Silero cannot run (no model yet, its process crashed).</summary>
public static class EnergyVad
{
    private const int Frame = 480;

    public static IReadOnlyList<(int Start, int End)> Detect(float[] samples, int count)
    {
        var frames = count / Frame;
        if (frames == 0) return Array.Empty<(int, int)>();
        var db = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var i = 0; i < Frame; i++)
            {
                var x = samples[f * Frame + i];
                sum += x * x;
            }
            db[f] = sum <= 0 ? -120 : 10 * Math.Log10(sum / Frame);
        }
        var sorted = db.OrderBy(x => x).ToArray();
        var floor = Math.Max(sorted[(int)(0.15 * (frames - 1))], -75);
        var speech = db.Select(x => x > floor + 12 && x > -48).ToArray();
        return Spans(speech, Frame, KitSegmenter.Rate, 0.45, 0.12, 0.2, count);
    }

    /// <summary>Per-frame speech flags to spans: gaps under minSilence merged, spans under minSpeech dropped, then padded.</summary>
    public static IReadOnlyList<(int Start, int End)> Spans(bool[] speech, int frame, int rate, double minSilence, double pad, double minSpeech, int total)
    {
        var raw = new List<(int S, int E)>();
        int? start = null;
        for (var f = 0; f <= speech.Length; f++)
        {
            var on = f < speech.Length && speech[f];
            if (on && start is null) start = f * frame;
            else if (!on && start is { } s)
            {
                raw.Add((s, f * frame));
                start = null;
            }
        }
        var merged = new List<(int S, int E)>();
        foreach (var r in raw)
        {
            if (merged.Count > 0 && r.S - merged[^1].E < minSilence * rate) merged[^1] = (merged[^1].S, r.E);
            else merged.Add(r);
        }
        var p = (int)(pad * rate);
        return merged.Where(m => m.E - m.S >= minSpeech * rate).Select(m => (Math.Max(0, m.S - p), Math.Min(total, m.E + p))).ToList();
    }
}

/// <summary>
/// The kit's segmenter (live.py process, lines 988-1010) for one source: 0.1 s reads; Silero every 0.3 s once
/// the buffer holds 2 s; cut 0.1 s after a pause of 0.45 s that follows speech; at 8 s cut at the longest inner
/// pause, else hard; a buffer of only silence over 2 s is dropped keeping its last 0.3 s. Silence never reaches
/// Whisper: a chunk is only ever cut out of a buffer the detector found speech in.
/// </summary>
public sealed class KitSegmenter
{
    public const int Rate = 16000;
    public const int Block = 1600;

    private readonly LiveSource _source;
    private readonly SpeechDetector _vad;
    private readonly float[] _block = new float[Block];
    private float[] _buf = new float[Rate * 12];
    private int _len;
    private int _blockFill;
    private long _fed;
    private double? _t0;
    private int _sinceVad;

    public KitSegmenter(LiveSource source, SpeechDetector vad)
    {
        _source = source;
        _vad = vad;
    }

    public double Pause { get; init; } = 0.45;
    public double MinSeg { get; init; } = 2.0;
    public double MaxSeg { get; init; } = 8.0;

    /// <summary>Times the detector was unavailable and loudness was used instead.</summary>
    public int Fallbacks { get; private set; }

    /// <summary>Recording time up to which this source has been decided: everything before is emitted or dropped as silence.</summary>
    public double DecidedUntil => _t0 ?? _fed / (double)Rate;

    public List<SpeechChunk> Push(ReadOnlySpan<float> samples)
    {
        var output = new List<SpeechChunk>();
        foreach (var s in samples)
        {
            _block[_blockFill++] = s;
            if (_blockFill == Block)
            {
                Process(output);
                _blockFill = 0;
            }
        }
        return output;
    }

    /// <summary>End of input: speech left in the buffer is handed on.</summary>
    public SpeechChunk? Flush()
    {
        if (_blockFill > 0)
        {
            Append(_block.AsSpan(0, _blockFill));
            _blockFill = 0;
        }
        if (_t0 is not { } t0 || _len < Rate * 0.3) return null;
        var sp = Detect();
        if (sp.Count == 0) return null;
        var end = Math.Min(_len, sp[^1].End + (int)(0.1 * Rate));
        return Emit(t0, end);
    }

    private void Append(ReadOnlySpan<float> x)
    {
        _fed += x.Length;
        if (_t0 is null) _t0 = (_fed - x.Length) / (double)Rate;
        if (_len + x.Length > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + x.Length));
        x.CopyTo(_buf.AsSpan(_len));
        _len += x.Length;
    }

    private IReadOnlyList<(int Start, int End)> Detect()
    {
        IReadOnlyList<(int Start, int End)>? sp;
        try { sp = _vad(_buf, _len); }
        catch (Exception ex)
        {
            Log.Warn($"speech detector failed: {ex.Message}");
            sp = null;
        }
        if (sp is null)
        {
            Fallbacks++;
            sp = EnergyVad.Detect(_buf, _len);
        }
        return sp;
    }

    private void Process(List<SpeechChunk> output)
    {
        Append(_block);
        var dur = _len / (double)Rate;
        _sinceVad++;
        if (dur < MinSeg || _sinceVad < 3) return;
        _sinceVad = 0;
        var sp = Detect();
        if (sp.Count == 0)
        {
            if (dur > 2.0) Drop(_len - (int)(0.3 * Rate));
            return;
        }
        var lastEnd = sp[^1].End;
        int? cut = null;
        if (_len - lastEnd >= Pause * Rate && lastEnd / (double)Rate >= MinSeg * 0.5)
            cut = Math.Min(_len, lastEnd + (int)(0.1 * Rate));
        else if (dur >= MaxSeg)
        {
            var best = -1;
            var bestGap = -1;
            for (var i = 0; i + 1 < sp.Count; i++)
            {
                var gap = sp[i + 1].Start - sp[i].End;
                if (gap > bestGap || (gap == bestGap && sp[i].End > best))
                {
                    bestGap = gap;
                    best = sp[i].End;
                }
            }
            cut = best >= 0 ? Math.Min(_len, best + (int)(0.05 * Rate)) : _len;
        }
        if (cut is { } c && c > 0 && _t0 is { } t0)
        {
            if (Emit(t0, c) is { } chunk) output.Add(chunk);
        }
    }

    private SpeechChunk? Emit(double t0, int cut)
    {
        var seg = _buf.AsSpan(0, cut).ToArray();
        Drop(cut);
        var peak = 0f;
        foreach (var x in seg) peak = Math.Max(peak, Math.Abs(x));
        if (peak < 0.01f) return null; // the kit's floor: a whisper of noise is not speech
        return new SpeechChunk(_source, TimeSpan.FromSeconds(t0), TimeSpan.FromSeconds(t0 + cut / (double)Rate), seg);
    }

    private void Drop(int n)
    {
        n = Math.Clamp(n, 0, _len);
        Array.Copy(_buf, n, _buf, 0, _len - n);
        _len -= n;
        _t0 = (_t0 ?? 0) + n / (double)Rate;
    }
}

// ================= falling behind =================

/// <summary>
/// What live text does when it falls behind: never a smaller model (Tiny turned a Vietnamese meeting into noise).
/// Queued chunks of one source are decoded together, up to 28 s, which costs Whisper about the same as one
/// (it always works on 30 s windows); only when it is over 3 minutes behind does it skip to the last minute,
/// and it says so.
/// </summary>
public static class CatchUp
{
    public const double MergeLagSeconds = 8;
    public const double MergeMaxSeconds = 28;
    public const double MergeGapSeconds = 1.5;
    public const double SkipLagSeconds = 180;
    public const double KeepSeconds = 60;

    public static string LagText(double lagSeconds) =>
        lagSeconds < 1.5 ? Loc.T("lag_live") : Loc.F("lag_behind", Math.Round(lagSeconds).ToString("0", CultureInfo.InvariantCulture));

    /// <summary>Takes the next chunks to decode as one (one source, in order) and those skipped; pending is changed in place.</summary>
    public static (List<SpeechChunk> Batch, List<SpeechChunk> Skipped) Take(List<SpeechChunk> pending, TimeSpan now)
    {
        var batch = new List<SpeechChunk>();
        var skipped = new List<SpeechChunk>();
        if (pending.Count == 0) return (batch, skipped);
        pending.Sort((a, b) => a.End.CompareTo(b.End));
        var lag = (now - pending[0].End).TotalSeconds;
        if (lag >= SkipLagSeconds)
        {
            var keepFrom = now - TimeSpan.FromSeconds(KeepSeconds);
            skipped = pending.Take(pending.Count - 1).Where(c => c.End < keepFrom).ToList();
            foreach (var s in skipped) pending.Remove(s);
            lag = (now - pending[0].End).TotalSeconds;
        }
        var first = pending[0];
        pending.RemoveAt(0);
        batch.Add(first);
        if (lag >= MergeLagSeconds)
        {
            var same = pending.Where(c => c.Source == first.Source).OrderBy(c => c.Start).ToList();
            foreach (var c in same)
            {
                var last = batch[^1];
                if ((c.Start - last.End).TotalSeconds > MergeGapSeconds || (c.End - first.Start).TotalSeconds > MergeMaxSeconds) break;
                batch.Add(c);
                pending.Remove(c);
            }
        }
        return (batch, skipped);
    }

    /// <summary>The chunks of a batch as one: gaps between them filled with silence.</summary>
    public static SpeechChunk Merge(IReadOnlyList<SpeechChunk> batch)
    {
        if (batch.Count == 1) return batch[0];
        var start = batch[0].Start;
        var end = batch[^1].End;
        var samples = new float[(int)Math.Round((end - start).TotalSeconds * KitSegmenter.Rate) + 1];
        var max = 0;
        foreach (var c in batch)
        {
            var at = (int)Math.Round((c.Start - start).TotalSeconds * KitSegmenter.Rate);
            var n = Math.Min(c.Samples.Length, samples.Length - at);
            if (n > 0) Array.Copy(c.Samples, 0, samples, at, n);
            max = Math.Max(max, at + n);
        }
        return new SpeechChunk(batch[0].Source, start, end, samples[..max]);
    }
}

// ================= the Silero process =================

/// <summary>
/// Silero voice activity detection (whisper.cpp ggml-silero) in its own small process (RecBox.exe --vad-worker),
/// shared by both live sources: a native crash ends only it, and the segmenter uses loudness until a new one is up.
/// Detect blocks the calling feed task (never the capture thread) for a few milliseconds.
/// </summary>
public sealed class VadService : IDisposable
{
    public const int MaxStarts = 3;

    private readonly string _model;
    private readonly Func<string, ProcessStartInfo> _launch;
    private readonly object _gate = new();
    private Process? _p;
    private int _starts;
    private int _nextId;
    private bool _disposed;

    public VadService(string modelPath, Func<string, ProcessStartInfo>? launch = null)
    {
        _model = modelPath;
        _launch = launch ?? DefaultLaunch;
    }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    public bool Available => !_disposed && _starts < MaxStarts + 1 && File.Exists(_model);

    public static ProcessStartInfo DefaultLaunch(string model)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("RecBox cannot find its own exe to start the speech detector.");
        var psi = new ProcessStartInfo(exe);
        foreach (var a in new[] { TranscriptionWorker.VadFlag, "--model", model, "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        return psi;
    }

    public sealed class Request
    {
        public int Id { get; set; }
        public string Pcm { get; set; } = "";
    }

    public sealed class Reply
    {
        public int Id { get; set; }
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public List<int[]> Spans { get; set; } = new();
    }

    /// <summary>Speech spans in samples, or null (not started, crashed, too slow): the caller falls back to loudness.</summary>
    public IReadOnlyList<(int Start, int End)>? Detect(float[] samples, int count)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            var p = EnsureStarted();
            if (p is null) return null;
            try
            {
                var pcm = new byte[count * 2];
                for (var i = 0; i < count; i++)
                {
                    var v = (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
                    pcm[2 * i] = (byte)v;
                    pcm[2 * i + 1] = (byte)(v >> 8);
                }
                var id = ++_nextId;
                p.StandardInput.WriteLine(JsonSerializer.Serialize(new Request { Id = id, Pcm = Convert.ToBase64String(pcm) }, TranscriptionWorker.Json));
                p.StandardInput.Flush();
                var read = p.StandardOutput.ReadLineAsync();
                while (true)
                {
                    if (!read.Wait(Timeout)) throw new TimeoutException("the speech detector did not answer");
                    var line = read.Result;
                    if (line is null) throw new IOException("the speech detector ended");
                    if (line.StartsWith("@vad ", StringComparison.Ordinal))
                    {
                        var reply = JsonSerializer.Deserialize<Reply>(line[5..], TranscriptionWorker.Json);
                        if (reply?.Id == id)
                            return reply.Ok ? reply.Spans.Where(s => s.Length == 2).Select(s => (s[0], s[1])).ToList() : null;
                    }
                    read = p.StandardOutput.ReadLineAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"speech detector: {ex.Message}; using loudness until it is back");
                Kill();
                return null;
            }
        }
    }

    private Process? EnsureStarted()
    {
        if (_p is { HasExited: false } live) return live;
        Kill();
        if (_starts >= MaxStarts || !File.Exists(_model)) return null;
        _starts++;
        try
        {
            var psi = _launch(_model);
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardInputEncoding = new UTF8Encoding(false);
            var p = new Process { StartInfo = psi };
            var pid = 0;
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) Log.Worker($"[{pid} vad err] {e.Data}");
            };
            p.Start();
            pid = p.Id;
            WorkerPriority.Lower(p);
            p.BeginErrorReadLine();
            var ready = p.StandardOutput.ReadLineAsync();
            if (!ready.Wait(TimeSpan.FromSeconds(20)) || ready.Result is not { } line || !line.StartsWith("@ready", StringComparison.Ordinal) || line.Contains("\"ok\":false"))
            {
                Log.Warn($"speech detector {pid} did not start: {(ready.IsCompleted ? ready.Result : "no answer")}");
                try { p.Kill(true); } catch { }
                p.Dispose();
                return null;
            }
            Log.Info($"speech detector {pid} started (Silero, {Path.GetFileName(_model)})");
            _p = p;
            return p;
        }
        catch (Exception ex)
        {
            Log.Warn($"speech detector could not start: {ex.Message}");
            return null;
        }
    }

    private void Kill()
    {
        var p = _p;
        _p = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(true); } catch { }
        try { p.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            var p = _p;
            _p = null;
            if (p is null) return;
            try
            {
                p.StandardInput.Close();
                if (!p.WaitForExit(1000)) p.Kill(true);
            }
            catch { }
            try { p.Dispose(); } catch { }
        }
    }
}

/// <summary>The Silero model file (ggml, about 0.9 MB), downloaded once next to the Whisper models.</summary>
public static class SileroModel
{
    public const string FileName = "ggml-silero-v5.1.2.bin";

    public static string Path => System.IO.Path.Combine(AppPaths.ModelsDir, FileName);

    public static bool IsPresent => File.Exists(Path);

    public static async Task DownloadAsync(CancellationToken ct)
    {
        if (IsPresent) return;
        Directory.CreateDirectory(AppPaths.ModelsDir);
        var tmp = Path + ".download";
        Log.Info("downloading the Silero speech detector");
        await using (var src = await Whisper.net.Ggml.WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(Whisper.net.Ggml.SileroVadType.V5_1_2, ct))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst, ct);
        File.Move(tmp, Path, overwrite: true);
        Log.Info($"Silero saved to {Path} ({new FileInfo(Path).Length} bytes)");
    }
}

/// <summary>Speech of a whole track grouped into windows of at most 30 s (faster-whisper's vad_filter and collect_chunks).</summary>
public static class SpeechWindows
{
    public sealed record Window(int[] Starts, int[] Ends)
    {
        public int Samples => Starts.Zip(Ends).Sum(p => p.Second - p.First);

        /// <summary>A time inside the window's audio (speech only, gaps removed) back to the track's time, in samples.</summary>
        public int ToTrack(double seconds)
        {
            var at = (int)Math.Round(seconds * KitSegmenter.Rate);
            for (var i = 0; i < Starts.Length; i++)
            {
                var len = Ends[i] - Starts[i];
                if (at <= len || i == Starts.Length - 1) return Starts[i] + Math.Min(at, len);
                at -= len;
            }
            return Ends.Length > 0 ? Ends[^1] : 0;
        }

        public float[] Audio(float[] track)
        {
            var a = new float[Samples];
            var o = 0;
            for (var i = 0; i < Starts.Length; i++)
            {
                Array.Copy(track, Starts[i], a, o, Ends[i] - Starts[i]);
                o += Ends[i] - Starts[i];
            }
            return a;
        }
    }

    public static List<Window> Group(IReadOnlyList<(int Start, int End)> spans, double maxSeconds = 30)
    {
        var max = (int)(maxSeconds * KitSegmenter.Rate);
        var windows = new List<Window>();
        var s = new List<int>();
        var e = new List<int>();
        var total = 0;
        foreach (var (a0, b0) in spans)
        {
            for (var a = a0; a < b0;)
            {
                var b = Math.Min(b0, a + max);
                if (total + (b - a) > max && s.Count > 0)
                {
                    windows.Add(new Window(s.ToArray(), e.ToArray()));
                    s.Clear();
                    e.Clear();
                    total = 0;
                }
                s.Add(a);
                e.Add(b);
                total += b - a;
                a = b;
            }
        }
        if (s.Count > 0) windows.Add(new Window(s.ToArray(), e.ToArray()));
        return windows;
    }
}
