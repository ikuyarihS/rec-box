using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RecBox.Core.Coaching;

/// <summary>
/// Who the two tracks are and the brief's name spellings, from the meeting brief (Me: / Them: lines, Hotwords, names).
/// Kept in session.json, so every saved text uses them: live-transcript.txt / .json, transcript.md / .srt, and a later
/// Transcribe again. Null when the brief names nobody.
/// </summary>
public sealed class SpeakerNames
{
    private IReadOnlyList<NameTarget>? _targets;

    /// <summary>The user (microphone track); null: "You".</summary>
    public string? You { get; set; }
    /// <summary>The other side (system audio track); null: "Others".</summary>
    public string? Others { get; set; }
    /// <summary>Names the kit's name fix writes the brief's way ("Flow 3D" becomes Floor3D).</summary>
    public List<string> Names { get; set; } = new();

    public static SpeakerNames? From(string? goal, string? brief)
    {
        var (you, others) = BriefNames.Parse(brief);
        var targets = BriefNames.NameTargets(BriefNames.Terms((goal ?? "") + "\n" + (brief ?? "")));
        if (you is null && others is null && targets.Count == 0) return null;
        return new SpeakerNames { You = you, Others = others, Names = targets.Select(t => t.Name).ToList() };
    }

    /// <summary>"You" / "Others" as the brief names them; any other speaker as it is.</summary>
    public string Label(string speaker) => speaker == "You" ? You ?? "You" : speaker == "Others" ? Others ?? "Others" : speaker;

    /// <summary>The brief's spelling of its names in a line of speech.</summary>
    public string Fix(string text)
    {
        _targets ??= BriefNames.NameTargets(Names);
        return _targets.Count == 0 ? text : BriefNames.FixNames(text, _targets).Text;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Legend => $"{Label("You")} = microphone, {Label("Others")} = system audio";

    /// <summary>The legend for files: "You = microphone, Others = system audio" when there are no names.</summary>
    public static string LegendOf(SpeakerNames? n) => n?.Legend ?? "You = microphone, Others = system audio";
}

/// <summary>A name from the brief the name fix may write: its spelling, its letters-and-digits key, its digit part.</summary>
public sealed record NameTarget(string Name, string Key, string Digits);

/// <summary>
/// What the meeting brief says about the people and the words of the meeting (the kit's LEAD file): the user's name and the
/// other side's names for the Me / Remote lines, the terms that wake the coach, the questions to ask, and the kit's name fix
/// (live.py fix_names: "Flow 3D" -> Floor3D, "Triple A" -> TripleA).
/// </summary>
public static class BriefNames
{
    private static readonly Regex MeLine = new(@"^\s*(?:[-*]\s*)?\**\s*(me|you|i am|my name|us|we|our side)\s*\**\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex ThemLine = new(@"^\s*(?:[-*]\s*)?\**\s*(them|others|other side|remote|they|their side|attendees)\s*\**\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex Tok = new(@"[^\W_]+", RegexOptions.Compiled);
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "this", "that", "they", "them", "their", "our", "you", "your", "what", "when", "where", "which", "who", "why",
        "how", "can", "could", "will", "would", "should", "does", "did", "are", "was", "were", "has", "have", "had", "not", "but", "from", "into",
        "about", "ask", "goal", "question", "questions", "answer", "notes", "live", "names", "people", "terms", "brief", "meeting", "me", "others",
        "remote", "role", "next", "step", "steps", "todo", "tbd", "tbc", "cua", "cho", "cac", "nhung", "mot", "duoc", "khong",
    };

    public static (string? You, string? Others) Parse(string? brief)
    {
        if (string.IsNullOrWhiteSpace(brief)) return (null, null);
        static string? Clean(Match m) => m.Success && m.Groups[2].Value.Trim().Trim('*').Trim() is { Length: > 0 } v ? (v.Length > 60 ? v[..60] : v) : null;
        return (Clean(MeLine.Match(brief)), Clean(ThemLine.Match(brief)));
    }

    /// <summary>Names and terms: a "Hotwords:" line first, then capitalised words, acronyms and words with digits (3+ characters).</summary>
    public static IReadOnlyList<string> Terms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var list = new List<string>();
        var hot = Regex.Match(text, @"^\s*hotwords\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (hot.Success) list.AddRange(hot.Groups[1].Value.Split(',').Select(x => x.Trim()).Where(x => x.Length >= 3));
        foreach (Match m in Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}.\-]{2,}"))
        {
            var w = m.Value.TrimEnd('.', '-');
            if (w.Length < 3 || Stop.Contains(w)) continue;
            if (char.IsUpper(w[0]) || w.Any(char.IsDigit) && w.Any(char.IsLetter)) list.Add(w);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).Take(80).ToList();
    }

    /// <summary>The questions to ask: the rows under a "Questions" heading (bullets, numbers or a table), else lines that end with "?".</summary>
    public static IReadOnlyList<string> Questions(string? brief)
    {
        var outList = new List<string>();
        if (string.IsNullOrWhiteSpace(brief)) return outList;
        var lines = brief.Replace("\r", "").Split('\n');
        var inQ = false;
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            var heading = Regex.IsMatch(l, @"^#{1,6}\s") || Regex.IsMatch(l, @"^[^|?]{1,40}:\s*$");
            if (heading)
            {
                inQ = Regex.IsMatch(CoachGate.Fold(l), @"question|to ask|cau hoi|hoi|q\s*/\s*a");
                continue;
            }
            if (!inQ || l.Length == 0) continue;
            string? q = null;
            if (l.StartsWith('|'))
            {
                if (Regex.IsMatch(l, @"^\|\s*:?-{2,}")) continue;
                var cells = l.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length == 0) continue;
                var idLike = Regex.IsMatch(cells[0], @"^#?\s*[A-Za-z]*\s*\d+$") || cells[0] == "#";
                q = idLike && cells.Length > 1 ? cells[1] : cells[0];
                if (Regex.IsMatch(CoachGate.Fold(q), @"^(question|cau hoi|#)$")) continue;
            }
            else if (Regex.Match(l, @"^(?:[-*•]|\d+[.)])\s+(.+)$") is { Success: true } b) q = b.Groups[1].Value;
            if (q is { Length: > 2 }) outList.Add(q.Length > 200 ? q[..200] : q);
        }
        if (outList.Count == 0)
            outList.AddRange(lines.Select(l => Regex.Replace(l.Trim(), @"^(?:[-*•]|\d+[.)])\s+", "")).Where(l => l.EndsWith('?') && l.Length > 5).Select(l => l.Length > 200 ? l[..200] : l));
        return outList.Distinct().Take(30).ToList();
    }

    private static string Digits(string key) => string.Concat(Regex.Matches(key, @"\d[0-9a-z]*").Select(m => m.Value));

    /// <summary>The names the fix may write: ASCII names of 4+ letters or digits. Short acronyms and Vietnamese words are never targets.</summary>
    public static IReadOnlyList<NameTarget> NameTargets(IEnumerable<string> terms)
    {
        var outList = new List<NameTarget>();
        var seen = new HashSet<string>();
        foreach (var raw in terms)
        {
            var t = raw.Trim();
            var k = Regex.Replace(t, @"[\W_]+", "").ToLowerInvariant();
            if (Regex.IsMatch(t, @"[./\\_]") || t.Count(c => c == '-') > 1 || t.Length > 24) continue;
            if (k.Length >= 4 && t.All(c => c < 128) && Regex.IsMatch(k, "[a-z]") && seen.Add(k)) outList.Add(new NameTarget(t, k, Digits(k)));
        }
        return outList;
    }

    private static int Lev(string a, string b, int cap = 3)
    {
        if (Math.Abs(a.Length - b.Length) > cap) return cap + 1;
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++) cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] != b[j - 1] ? 1 : 0));
            if (cur.Min() > cap) return cap + 1;
            prev = cur;
        }
        return prev[^1];
    }

    /// <summary>
    /// The kit's fix_names: spans of 1-3 words that are a brief name split or misspelt, written the brief's way. A span qualifies
    /// only when its digits equal the name's and either it is 2-3 capitalised words that join to exactly the name, or it carries
    /// the name's digits and misses it by at most 1 letter (2 for names of 7+). One ordinary word is never rewritten.
    /// </summary>
    public static (string Text, int Fixed) FixNames(string text, IReadOnlyList<NameTarget> targets)
    {
        if (targets.Count == 0 || string.IsNullOrEmpty(text)) return (text, 0);
        var toks = Tok.Matches(text).ToList();
        var sb = new StringBuilder();
        int pos = 0, n = 0, i = 0;
        while (i < toks.Count)
        {
            (int W, string Name)? hit = null;
            foreach (var w in new[] { 3, 2, 1 })
            {
                if (i + w > toks.Count) continue;
                var span = toks.GetRange(i, w);
                var badGap = false;
                for (var j = 0; j < w - 1; j++)
                {
                    var g = text[(span[j].Index + span[j].Length)..span[j + 1].Index];
                    if (!Regex.IsMatch(g, @"^[ .\-]{1,2}$") || (g.Contains('.') && g.Contains(' '))) badGap = true;
                }
                if (badGap) continue;
                var words = span.Select(t => t.Value).ToList();
                var key = string.Concat(words).ToLowerInvariant();
                var dig = Digits(key);
                var look = words.Where(x => !char.IsDigit(x[0])).All(x => char.IsUpper(x[0]));
                var lett = key.Count(c => char.IsLetter(c)) >= 2;
                foreach (var t in targets)
                {
                    if (dig != t.Digits) continue;
                    var d = key == t.Key ? 0 : t.Digits.Length > 0 ? Lev(key, t.Key) : 9;
                    var lim = t.Key.Length >= 7 ? 2 : 1;
                    var ok = (w >= 2 && look && d == 0)
                             || (t.Digits.Length > 0 && lett && (w == 1 || look) && d <= lim)
                             || (t.Digits.Length > 0 && lett && w >= 2 && !look && d <= lim && key.Length >= 2 && t.Key.Length >= 2 && key[..2] == t.Key[..2]
                                 && words.Where(x => !char.IsDigit(x[0])).All(x => x.Length >= 3));
                    if (ok)
                    {
                        hit = (w, t.Name);
                        break;
                    }
                }
                if (hit is not null) break;
            }
            if (hit is { } h)
            {
                int a = toks[i].Index, b = toks[i + h.W - 1].Index + toks[i + h.W - 1].Length;
                if (text[a..b] != h.Name)
                {
                    sb.Append(text, pos, a - pos).Append(h.Name);
                    pos = b;
                    n++;
                }
                i += h.W;
            }
            else i++;
        }
        sb.Append(text, pos, text.Length - pos);
        return (sb.ToString(), n);
    }
}

/// <summary>The kit's retranscribe.py repair: a killed writer leaves the RIFF and data sizes at 0; rewrite them from the file length.</summary>
public static class WavRepair
{
    /// <summary>True when the header was repaired; false when it was already right or the file is not a RIFF WAV.</summary>
    public static bool Fix(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var size = f.Length;
        var head = new byte[12];
        if (f.Read(head, 0, 12) < 12 || Encoding.ASCII.GetString(head, 0, 4) != "RIFF") return false;
        var hdr = new byte[8];
        while (f.Read(hdr, 0, 8) == 8)
        {
            var id = Encoding.ASCII.GetString(hdr, 0, 4);
            var len = BitConverter.ToUInt32(hdr, 4);
            if (id == "data")
            {
                var dataOff = f.Position;
                var want = size - dataOff;
                if (want > uint.MaxValue || len == want) return false;
                f.Position = 4;
                f.Write(BitConverter.GetBytes((uint)(size - 8)));
                f.Position = dataOff - 4;
                f.Write(BitConverter.GetBytes((uint)want));
                return true;
            }
            f.Seek(len + (len & 1), SeekOrigin.Current);
        }
        return false;
    }

    /// <summary>Every *.wav in a folder (or one file): the names whose header was repaired.</summary>
    public static IReadOnlyList<string> FixAll(string path)
    {
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.wav") : new[] { path };
        var fixedList = new List<string>();
        foreach (var f in files)
        {
            try
            {
                if (Fix(f)) fixedList.Add(Path.GetFileName(f));
            }
            catch (IOException ex)
            {
                Log.Warn($"wav repair: {Path.GetFileName(f)} skipped ({ex.Message})");
            }
        }
        return fixedList;
    }
}

/// <summary>The kit's slide signature (meet-watch.ps1): the slide area (8-74 % wide, 12-74 % high) as 32x18 grey levels.</summary>
public static class SlideSignature
{
    public const int W = 32, H = 18;

    public static int[] Of(byte[] bgra, int width, int height, int stride)
    {
        var sig = new int[W * H];
        int x0 = (int)(width * 0.08), y0 = (int)(height * 0.12), cw = Math.Max(W, (int)(width * 0.66)), ch = Math.Max(H, (int)(height * 0.62));
        for (var j = 0; j < H; j++)
            for (var i = 0; i < W; i++)
            {
                int xa = x0 + i * cw / W, xb = Math.Min(width, x0 + (i + 1) * cw / W), ya = y0 + j * ch / H, yb = Math.Min(height, y0 + (j + 1) * ch / H);
                long sum = 0, cnt = 0;
                for (var y = ya; y < yb; y++)
                    for (var x = xa; x < xb; x++)
                    {
                        var p = y * stride + x * 4;
                        sum += (bgra[p] + bgra[p + 1] + bgra[p + 2]) / 3;
                        cnt++;
                    }
                sig[j * W + i] = cnt > 0 ? (int)(sum / cnt) : 0;
            }
        return sig;
    }

    public static double Diff(int[] a, int[] b)
    {
        double s = 0;
        for (var k = 0; k < a.Length; k++) s += Math.Abs(a[k] - b[k]);
        return s / a.Length;
    }
}

/// <summary>
/// meet-watch.ps1's rule: a frame is a new slide when it differs from the last frame by at least the threshold AND from every
/// slide already saved (a slide shown again stays silent). Dark frames (average under 40: a video tile, a black screen) are skipped.
/// </summary>
public sealed class SlideDeduper
{
    private readonly double _threshold;
    private readonly List<int[]> _seen = new();
    private int[]? _last;

    public SlideDeduper(double threshold = 6.0) => _threshold = threshold;

    public bool Accept(int[] sig)
    {
        if (sig.Average() < 40) return false;
        if (_last is not null && SlideSignature.Diff(sig, _last) < _threshold) return false;
        _last = sig;
        if (_seen.Any(o => SlideSignature.Diff(sig, o) < _threshold)) return false;
        _seen.Add(sig);
        return true;
    }
}

/// <summary>A small PNG writer (RGB, no filter, zlib) so the core needs no imaging library.</summary>
public static class PngWriter
{
    private static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static void Write(string path, byte[] bgra, int width, int height, int stride)
    {
        using var f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        Be(ihdr, 0, (uint)width);
        Be(ihdr, 4, (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        Chunk(f, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0;
                for (var x = 0; x < width; x++)
                {
                    var p = y * stride + x * 4;
                    row[1 + x * 3] = bgra[p + 2];
                    row[2 + x * 3] = bgra[p + 1];
                    row[3 + x * 3] = bgra[p];
                }
                z.Write(row);
            }
        }
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", Array.Empty<byte>());
    }

    private static void Be(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        Be(len, 0, (uint)data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var c = 0xFFFFFFFFu;
        foreach (var b in t.Concat(data)) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        var crc = new byte[4];
        Be(crc, 0, c ^ 0xFFFFFFFFu);
        s.Write(crc);
    }
}

/// <summary>
/// The kit's slide capture (slide-cap.ps1 / meet-watch.ps1) for the app: every N seconds, the largest visible window whose
/// title holds the given text (never RecBox's own) is drawn with PrintWindow (works when covered, not when minimized), and a
/// PNG is saved in &lt;recording&gt;\slides only for a new slide. Local only; off unless the user turns it on.
/// </summary>
public sealed class SlideCapture : IDisposable
{
    public const string Folder = "slides";
    private readonly string _dir;
    private readonly string _title;
    private readonly Func<TimeSpan> _clock;
    private readonly SlideDeduper _dedupe = new();
    private readonly Timer _timer;
    private readonly object _lock = new();
    private bool _stopped;
    private int _misses;

    public SlideCapture(string recordingDir, string titleContains, Func<TimeSpan> clock, double everySeconds = 10)
    {
        _dir = Path.Combine(recordingDir, Folder);
        _title = titleContains.Trim();
        _clock = clock;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(Math.Max(2, everySeconds)));
    }

    public List<(double At, string File)> Saved { get; } = new();
    public event Action<string>? Note;

    private void Tick()
    {
        lock (_lock)
        {
            if (_stopped || _title.Length == 0) return;
            try
            {
                var h = FindWindow(_title);
                if (h == IntPtr.Zero)
                {
                    if (++_misses == 3) Note?.Invoke(Loc.F("slides_no_window", _title));
                    return;
                }
                _misses = 0;
                var shot = Grab(h);
                if (shot is null) return;
                var (px, w, ht) = shot.Value;
                if (!_dedupe.Accept(SlideSignature.Of(px, w, ht, w * 4))) return;
                Directory.CreateDirectory(_dir);
                var at = _clock();
                var name = $"slide-{at:hh\\-mm\\-ss}.png";
                PngWriter.Write(Path.Combine(_dir, name), px, w, ht, w * 4);
                Saved.Add((at.TotalSeconds, name));
                Log.Info($"slides: saved {name} ({w}x{ht})");
            }
            catch (Exception ex)
            {
                Log.Warn($"slides: capture failed: {ex.Message}");
            }
        }
    }

    /// <summary>Stops capturing; a capture in progress finishes first. The slides saved, oldest first.</summary>
    public IReadOnlyList<(double At, string File)> Stop()
    {
        _timer.Dispose();
        lock (_lock)
        {
            _stopped = true;
            return Saved.ToList();
        }
    }

    public void Dispose() => Stop();

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int L, T, R, B;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    private static IntPtr FindWindow(string title)
    {
        var own = (uint)Environment.ProcessId;
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == own) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) return true;
            GetWindowRect(h, out var r);
            long area = (long)(r.R - r.L) * (r.B - r.T);
            if (area > bestArea)
            {
                bestArea = area;
                best = h;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static (byte[] Pixels, int W, int H)? Grab(IntPtr h)
    {
        GetWindowRect(h, out var r);
        int w = r.R - r.L, ht = r.B - r.T;
        if (w < 200 || ht < 200 || w > 10000 || ht > 10000) return null;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bmi = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = w, Height = -ht, Planes = 1, BitCount = 32 };
        var bmp = CreateDIBSection(dc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bmp == IntPtr.Zero) return null;
            var old = SelectObject(dc, bmp);
            var ok = PrintWindow(h, dc, 2); // PW_RENDERFULLCONTENT: browser and GPU windows too
            SelectObject(dc, old);
            if (!ok) return null;
            var px = new byte[w * ht * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            return (px, w, ht);
        }
        finally
        {
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}

/// <summary>
/// A saved recording's live text (live-transcript.json) through the coach on a simulated meeting clock: each line arrives
/// at the time live text showed it, the coach wakes as it would have, and coach.md / coach.json are written to the output
/// folder. RecBox.exe --coach-replay; with the rule-based provider nothing leaves the PC.
/// </summary>
public static class CoachReplay
{
    private static readonly System.Text.Json.JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The recording's languages as the app gives them to the gate (lines in other scripts are junk): the same rule as
    /// live (<see cref="CoachLanguages.ForGate"/>): the set language, or the locked one, plus English; the kit's set before a lock.
    /// </summary>
    public static IReadOnlyList<string> Languages(string liveJson)
    {
        var doc = System.Text.Json.JsonSerializer.Deserialize<Transcription.LiveTranscriptStore.JsonDoc>(File.ReadAllText(liveJson), Read);
        return CoachLanguages.ForGate(doc?.LanguageSetting, doc?.LanguageDetected);
    }

    /// <summary>
    /// The lines as the coach read them live: who spoke comes from the track (Source mic = You, speaker = Others), never
    /// from the saved label, which holds the brief's names ("Lan", "Mr Sato") when the brief named people.
    /// </summary>
    public static IReadOnlyList<CoachLine> Lines(string liveJson)
    {
        var doc = System.Text.Json.JsonSerializer.Deserialize<Transcription.LiveTranscriptStore.JsonDoc>(File.ReadAllText(liveJson), Read)
                  ?? throw new InvalidDataException($"{liveJson} is empty");
        return doc.Lines
            .OrderBy(l => l.Emitted ?? l.End)
            .Select(l => new CoachLine(TimeSpan.FromSeconds(l.Emitted ?? l.End), SpeakerOf(l), l.Text, l.Echo, l.Source == "note" || (l.Source.Length == 0 && l.Speaker == "RecBox")))
            .ToList();
    }

    private static string SpeakerOf(Transcription.LiveTranscriptStore.JsonLine l) => l.Source switch
    {
        "mic" => "You",
        "speaker" => "Others",
        "note" => "RecBox",
        _ => l.Speaker, // files of builds before Source was written
    };

    /// <param name="maxMinutes">Only the first minutes of the meeting (0 = all).</param>
    public static async Task<CoachSnapshot> RunAsync(IReadOnlyList<CoachLine> lines, CoachSettings settings, ICoachProvider provider,
        string outDir, string sessionId, double maxMinutes = 0, Action<string>? say = null, IEnumerable<string>? languages = null)
    {
        var now = TimeSpan.Zero;
        using var engine = new CoachEngine(new CoachOptions
        {
            Provider = provider,
            Setup = CoachSetup.From(settings),
            Clock = () => now,
            AutoPump = false,
            MaxWakes = Math.Max(1, settings.MaxWakes),
            Languages = languages,
        });
        var posted = 0;
        foreach (var l in lines)
        {
            if (maxMinutes > 0 && l.At.TotalMinutes > maxMinutes) break;
            if (l.At > now) now = l.At;
            engine.Pump();
            await engine.WhenIdleAsync().ConfigureAwait(false);
            engine.Post(l);
            posted++;
        }
        now += TimeSpan.FromSeconds(10);
        engine.Pump();
        await engine.WhenIdleAsync().ConfigureAwait(false);
        var snap = await engine.StopAsync(TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        Directory.CreateDirectory(outDir);
        CoachStore.Save(outDir, sessionId, snap);
        say?.Invoke($"{posted} live line(s), {engine.Wakes} wake(s), {snap.Calls} call(s), {snap.Failures} failure(s), {snap.Cues.Count} cue(s) " +
                    $"({snap.ThrottledCues} held back by the 3-minute limit), {snap.Questions.Count} question(s), {snap.NextSteps.Count} next step(s), {snap.Notes.Count} note(s)");
        return snap;
    }
}
