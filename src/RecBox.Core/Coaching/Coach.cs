using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RecBox.Core.Coaching;

// The live coach, the kit's listener (live-gate.mjs + live-listen.mjs + lead-write.mjs) as part of the app: a free local
// gate drops fillers, repeats, junk and echo-only mic lines; lines said together (2.5 s) go in one wake; each wake is one
// lean model call whose brief is rebuilt from the coach's own state plus the last 3 minutes of transcript, so the context
// never grows. Replies use the kit's grammar (LOG / QA / NEXT / CUE / ESCALATE / SKIP).

public enum CoachProviderKind
{
    /// <summary>Claude Code CLI installed on this PC, run headless (its own sign-in; no key in RecBox).</summary>
    ClaudeCli,
    /// <summary>Legacy (1.3.0 previews): the Anthropic API with a key. Read only: settings holding it load as <see cref="ClaudeCli"/>.</summary>
    Anthropic,
    /// <summary>An OpenAI-compatible model server on this PC (Ollama, LM Studio): fully offline. Only local addresses are used.</summary>
    OpenAiCompatible,
    /// <summary>A rule-based stand-in (replay, screenshots, tests). Never sends anything.</summary>
    Fake,
    /// <summary>OpenAI's Codex CLI installed on this PC, run headless (codex exec, its own sign-in).</summary>
    CodexCli,
    /// <summary>Google's Gemini CLI installed on this PC, run headless (gemini -p, its own sign-in).</summary>
    GeminiCli,
}

/// <summary>A Claude model the coach can suggest for Claude Code, with its speed tier (no prices: RecBox counts tokens only).</summary>
public sealed record CoachModel(string Id, string Name, string TierKey)
{
    public string Tier => Loc.T(TierKey);
    public string Display => $"{Name} - {Tier}";
    public override string ToString() => Id;
}

public static class CoachModels
{
    public const string Default = "claude-sonnet-5-5";

    public static readonly IReadOnlyList<CoachModel> Claude = new[]
    {
        new CoachModel("claude-haiku-5-5", "Haiku 5.5", "model_tier_fast"),
        new CoachModel(Default, "Sonnet 5.5", "model_tier_balanced"),
        new CoachModel("claude-opus-5-5", "Opus 5.5", "model_tier_strong"),
    };

    public static CoachModel? Find(string? id) => Claude.FirstOrDefault(m => m.Id == id);

    /// <summary>A model for the screen: the Claude model's name, the id as typed, or the tool's own default.</summary>
    public static string Describe(string? id) => string.IsNullOrWhiteSpace(id) ? Loc.T("model_cli_default") : Find(id)?.Name ?? id.Trim();
}

/// <summary>
/// Token figures before a recording starts, for the screen (no prices anywhere). The coach: about one look a minute in
/// a talkative meeting (the 10:16 replay woke 14 times in 10 minutes), each look its system prompt plus about 2,500
/// characters of state and transcript in and about 60 tokens out. Translation: about 10 lines a minute, 8 per call.
/// </summary>
public static class UsageEstimate
{
    public const int CoachLooksPerHour = 80;
    public const int TranslationCallsPerHour = 75;

    public static long CoachTokensPerHour(CoachSettings s)
    {
        var system = CoachPrompt.System(CoachSetup.From(s)).Length;
        return CoachLooksPerHour * ((system + 2500) / 4 + 60);
    }

    public static long TranslationTokensPerHour() => TranslationCallsPerHour * ((TextTranslator.InstructionChars + 8 * 90) / 4 + 8 * 30);

    /// <summary>"about 120,000 tokens an hour (estimate)".</summary>
    public static string Text(long tokens) => Loc.F("usage_per_hour", tokens.ToString("#,0", CultureInfo.InvariantCulture));

    /// <summary>Chars / 4: the estimate used when a tool reports no usage.</summary>
    public static long FromChars(int chars) => Math.Max(1, chars / 4);
}

/// <summary>How the coach should behave: a preset, plus the user's own words.</summary>
public sealed record CoachStyle(string Key, string Name, string Instruction)
{
    /// <summary>The name on screen, in the interface language (<see cref="Name"/> stays English: it goes in the prompt).</summary>
    public string Display => Loc.T("style_" + Key);
    public override string ToString() => Display;
}

public static class CoachStyles
{
    public static readonly IReadOnlyList<CoachStyle> All = new[]
    {
        new CoachStyle("balanced", "Balanced coach", "Track answers to the brief's questions, next steps and key facts; cue the user only when they should act in the next minute."),
        new CoachStyle("sales", "Sales or partner meeting", "Focus on the other side's needs, budget, timeline, decision makers and objections; cue questions that move the deal forward; never promise a price or a delivery that was not said."),
        new CoachStyle("interview", "Interview", "Track how each answer covers the brief's questions, flag vague or contradicting answers, cue follow-up questions."),
        new CoachStyle("technical", "Technical review", "Track decisions, risks, open technical questions and owners; flag claims that contradict the brief."),
        new CoachStyle("notes", "Notes only", "Take notes and fill the questions table; write a CUE only when the other side asks the user's side directly."),
    };

    public static CoachStyle Get(string? key) => All.FirstOrDefault(s => s.Key == key) ?? All[0];
}

/// <summary>The language the coach writes in.</summary>
public static class CoachLanguages
{
    public static readonly IReadOnlyList<(string Code, string Name)> All = new[]
    {
        ("same", "Same as the meeting"), ("en", "English"), ("vi", "Vietnamese"), ("ja", "Japanese"), ("ko", "Korean"),
        ("zh", "Chinese"), ("fr", "French"), ("de", "German"), ("es", "Spanish"),
    };

    /// <summary>The English name, for the prompt (never localized: the model reads it).</summary>
    public static string Name(string? code) => All.FirstOrDefault(l => l.Code == code).Name ?? All[0].Name;

    /// <summary>The name on screen, in the interface language.</summary>
    public static string Display(string? code) => code is null or "same" ? Loc.T("coachlang_same") : Loc.T("lang_" + code);

    /// <summary>
    /// The languages the coach's junk filter accepts (the kit always runs it with vi,en,ja): a set language plus English;
    /// with Auto, the language live text locked plus English once it locked, and the kit's default set before that.
    /// The live coach and the replay both take it from here.
    /// </summary>
    public static IReadOnlyList<string> ForGate(string? setting, string? locked)
    {
        var spec = !Transcription.Languages.IsAuto(setting) ? setting : locked is { Length: > 0 } l && l != Transcription.Languages.Auto ? l : null;
        if (spec is null) return KitDefault;
        var langs = Transcription.Languages.Allowed(spec, null).ToList();
        if (!langs.Contains("en")) langs.Add("en");
        return langs;
    }

    public static readonly IReadOnlyList<string> KitDefault = new[] { "vi", "en", "ja" };
}

/// <summary>The coach choices RecBox remembers between meetings (no keys: the AI tools use their own sign-in).</summary>
public sealed class CoachSettings
{
    /// <summary>Off by default: nothing is sent anywhere until the user turns the coach on.</summary>
    public bool Enabled { get; set; }
    public CoachProviderKind Provider { get; set; } = CoachProviderKind.ClaudeCli;
    /// <summary>The model for Claude Code (an id or alias; empty: Claude Code's own default).</summary>
    public string Model { get; set; } = CoachModels.Default;
    /// <summary>The model for Codex (empty: Codex's own default).</summary>
    public string CodexModel { get; set; } = "";
    /// <summary>The model for Gemini CLI (empty: Gemini CLI's own default).</summary>
    public string GeminiModel { get; set; } = "";
    /// <summary>Local OpenAI-compatible base URL, e.g. http://localhost:11434/v1 (Ollama) or http://localhost:1234/v1 (LM Studio).</summary>
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    /// <summary>The model name on the OpenAI-compatible endpoint.</summary>
    public string EndpointModel { get; set; } = "llama3.1";
    public string Goal { get; set; } = "";
    /// <summary>The meeting brief: who is there (names), terms, questions to ask, what not to promise.</summary>
    public string Brief { get; set; } = "";
    public string Role { get; set; } = "";
    public string Style { get; set; } = "balanced";
    public string Instructions { get; set; } = "";
    public string OutputLanguage { get; set; } = "same";
    /// <summary>Capture the shared screen of the meeting window when it changes (the kit's slide-cap), saved in slides\.</summary>
    public bool CaptureSlides { get; set; }
    /// <summary>Part of the meeting window's title, e.g. "Meet", "Zoom", "Teams".</summary>
    public string SlideWindow { get; set; } = "Meet";
    /// <summary>The file the brief was last loaded from (shown only; the brief text is what the coach uses).</summary>
    public string? BriefFile { get; set; }
    /// <summary>At most this many model calls per recording (cost cap).</summary>
    public int MaxWakes { get; set; } = 300;

    public CoachSettings Clone() => (CoachSettings)MemberwiseClone();

    /// <summary>The model typed for an AI tool (empty: the tool's own default).</summary>
    public string ModelFor(CoachProviderKind kind) => kind switch
    {
        CoachProviderKind.ClaudeCli or CoachProviderKind.Anthropic => Model ?? "",
        CoachProviderKind.CodexCli => CodexModel ?? "",
        CoachProviderKind.GeminiCli => GeminiModel ?? "",
        CoachProviderKind.OpenAiCompatible => EndpointModel ?? "",
        _ => "",
    };

    public void SetModelFor(CoachProviderKind kind, string model)
    {
        model = (model ?? "").Trim();
        switch (kind)
        {
            case CoachProviderKind.ClaudeCli or CoachProviderKind.Anthropic: Model = model; break;
            case CoachProviderKind.CodexCli: CodexModel = model; break;
            case CoachProviderKind.GeminiCli: GeminiModel = model; break;
            case CoachProviderKind.OpenAiCompatible: EndpointModel = model; break;
        }
    }

    /// <summary>Settings of 1.3.0 previews: the removed Anthropic API choice becomes Claude Code (no key needed).</summary>
    public void Migrate()
    {
        if (Provider == CoachProviderKind.Anthropic) Provider = CoachProviderKind.ClaudeCli;
        Model ??= CoachModels.Default;
        CodexModel ??= "";
        GeminiModel ??= "";
    }
}

/// <summary>
/// Says plainly where the text goes. "Nothing leaves your computer" only while that is true: an AI tool on this PC
/// (Claude Code, Codex, Gemini CLI) still sends the text to its vendor; a local model server keeps it here.
/// The coach and translation (to a language other than English) both use the picked AI.
/// </summary>
public static class CoachPrivacy
{
    public static string Offline => Loc.T("privacy_offline");

    public static bool IsLocalEndpoint(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)) return false;
        var h = u.Host.Trim('[', ']').ToLowerInvariant();
        return h is "localhost" or "::1" or "0.0.0.0" || h.StartsWith("127.", StringComparison.Ordinal) || h.EndsWith(".localhost", StringComparison.Ordinal);
    }

    /// <summary>True when the picked AI sends text outside this PC (whether or not anything uses it now).</summary>
    public static bool IsCloud(CoachSettings s) => s.Provider switch
    {
        CoachProviderKind.ClaudeCli or CoachProviderKind.Anthropic or CoachProviderKind.CodexCli or CoachProviderKind.GeminiCli => true,
        CoachProviderKind.OpenAiCompatible => !IsLocalEndpoint(s.Endpoint),
        _ => false,
    };

    /// <summary>True when the coach is on, or translation needs the AI, and that AI is outside this PC.</summary>
    public static bool SendsOut(CoachSettings s, string? translateTo = null) =>
        (s.Enabled || TranslateTargets.NeedsAi(translateTo)) && IsCloud(s);

    /// <summary>Who receives the text: "Claude Code (Anthropic)", "Codex (OpenAI)", "Gemini CLI (Google)".</summary>
    public static string Receiver(CoachSettings s) => s.Provider switch
    {
        CoachProviderKind.ClaudeCli or CoachProviderKind.Anthropic => Loc.T("recv_claude"),
        CoachProviderKind.CodexCli => Loc.T("recv_codex"),
        CoachProviderKind.GeminiCli => Loc.T("recv_gemini"),
        CoachProviderKind.OpenAiCompatible => Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var u) ? u.Host : Loc.T("recv_endpoint"),
        _ => Loc.T("recv_nobody"),
    };

    public static string Text(CoachSettings s, string? translateTo = null)
    {
        var translate = TranslateTargets.NeedsAi(translateTo);
        if (!s.Enabled && !translate) return Offline;
        if (!IsCloud(s)) return Loc.T("privacy_local_model");
        var what = s.Enabled && translate ? Loc.T("privacy_for_both") : s.Enabled ? Loc.T("privacy_for_coach") : Loc.T("privacy_for_translate");
        return Loc.F("privacy_sent", Receiver(s), what);
    }
}

/// <summary>
/// The coach of the recording in progress, as the screen must describe it. While a coach runs, the privacy line and the
/// summary name the AI it really uses (<see cref="Described"/>); another AI picked mid-recording becomes that AI only when
/// it can be made (tool installed, address valid), else the running one stays and the user is told why. A start that
/// failed (the tool missing, bad address) is tried again as soon as the cause may be fixed (choice changed, or every
/// 10 s), so the user never has to switch the coach off and on.
/// </summary>
public sealed class CoachRun
{
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);
    private readonly Func<CoachSettings, ICoachProvider> _create;
    private DateTime _lastTry;

    public CoachRun(Func<CoachSettings, ICoachProvider>? create = null) => _create = create ?? (s => CoachProviders.Create(s));

    public CoachEngine? Engine { get; private set; }
    /// <summary>The choice the running coach's AI was made from (null while none runs).</summary>
    public CoachSettings? Running { get; private set; }
    /// <summary>A start failed and is tried again (key saved, choice changed, or every <see cref="RetryEvery"/>).</summary>
    public bool Waiting { get; private set; }
    /// <summary>Why the last start failed and how it starts, for the panel (null after a start).</summary>
    public string? Error { get; private set; }

    /// <summary>Starts the coach; never throws for a setup problem (that becomes <see cref="Error"/> and a retry).</summary>
    public bool TryStart(CoachSettings c, Func<ICoachProvider, CoachEngine> build, DateTime? now = null)
    {
        if (Engine is not null) return true;
        _lastTry = now ?? DateTime.UtcNow;
        try
        {
            var p = _create(c);
            Engine = build(p);
            Running = c.Clone();
            Waiting = false;
            Error = null;
            return true;
        }
        catch (CoachProviderException ex)
        {
            Waiting = true;
            Error = $"{ex.Message} {Loc.T("coach_starts_itself")}";
            return false;
        }
    }

    /// <summary>
    /// The AI choice changed (provider, model, address) or a key was saved (force): a coach that could not start tries
    /// again; a running coach moves to the new AI when it can be made, else keeps its AI. Returns the panel note, or null.
    /// </summary>
    public string? Apply(CoachSettings c, Func<ICoachProvider, CoachEngine> build, bool force = false, DateTime? now = null)
    {
        if (Engine is null)
        {
            if (!Waiting || !c.Enabled) return null;
            return TryStart(c, build, now) ? Loc.F("coach_started", Engine!.Provider.Name) : Error;
        }
        if (!force && Running is { } r && SameAi(r, c)) return null;
        var engine = Engine;
        try
        {
            var p = _create(c);
            engine.SetProvider(p);
            Running = c.Clone();
            return Loc.F("coach_now_uses", CoachEngine.Describe(p));
        }
        catch (CoachProviderException ex)
        {
            return Loc.F("coach_keeps_using", engine.Provider.Name, ex.Message);
        }
    }

    public bool RetryDue(DateTime now) => Engine is null && Waiting && now - _lastTry >= RetryEvery;

    /// <summary>What the screen describes: the running coach's AI while one runs, else the picked one (the switch and the brief are always the picked ones).</summary>
    public CoachSettings Described(CoachSettings picked)
    {
        if (Running is not { } r) return picked;
        var d = picked.Clone();
        d.Provider = r.Provider;
        d.Model = r.Model;
        d.CodexModel = r.CodexModel;
        d.GeminiModel = r.GeminiModel;
        d.Endpoint = r.Endpoint;
        d.EndpointModel = r.EndpointModel;
        return d;
    }

    /// <summary>The recording stopped: returns the engine to finish (null when none ran) and forgets the run.</summary>
    public CoachEngine? Stop()
    {
        var e = Engine;
        Engine = null;
        Running = null;
        Waiting = false;
        Error = null;
        return e;
    }

    private static bool SameAi(CoachSettings a, CoachSettings b) =>
        a.Provider == b.Provider && a.ModelFor(a.Provider).Trim() == b.ModelFor(b.Provider).Trim() && (a.Endpoint ?? "").Trim() == (b.Endpoint ?? "").Trim();
}

/// <summary>One line the coach reads: live text (You / Others, echo tag) or a RecBox note ([SYS]).</summary>
public sealed record CoachLine(TimeSpan At, string Speaker, string Text, bool Echo = false, bool IsSys = false)
{
    public string Stamp => Stamps.Of(At);
}

internal static class Stamps
{
    public static string Of(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    public static string Of(double seconds) => Of(TimeSpan.FromSeconds(Math.Max(0, seconds)));
}

public sealed record GateResult(bool Wake, string[] Reasons)
{
    public bool Dropped { get; init; }
    public bool Sys { get; init; }
    /// <summary>Held lines that ride along with this wake (before the trigger).</summary>
    public IReadOnlyList<CoachLine> Bundle { get; init; } = Array.Empty<CoachLine>();
    public CoachLine? Trigger { get; init; }
}

/// <summary>
/// The kit's free wake filter (live-gate.mjs), no model, no network. Dropped: acknowledgement fillers, junk scripts and
/// outro hallucinations. Held (ride along with the next wake): echo-tagged mic lines, repeats within 2 min (other speaker:
/// same second only), yes/no answers and lines of 3 words or fewer. Wake: a question, a number, an action word, a tech
/// term, a brief term, a topic change, or 6 held lines (heartbeat).
/// </summary>
public sealed class CoachGate
{
    private static readonly Regex Q = new(@"\?|\b(khong a|khong ha|duoc khong|phai khong|dung khong|co khong|chua a|bao nhieu|bao gio|khi nao|the nao|lam sao|tai sao|vi sao|o dau|cai gi|la gi|nhu nao|hoi|cho em hoi|xin hoi|what|how|when|why|which|can you|could you|do you|is it|are you)\b|[？か]$|ですか|ますか|でしょうか", RegexOptions.Compiled);
    private static readonly Regex Num = new(@"\d|%|\b(tram|nghin|ngan|trieu|ty|ti|usd|vnd|dong|man[- ]?month|thang|tuan|quy|nam 20|deadline|han chot|thu (hai|ba|tu|nam|sau|bay)|chu nhat|ngay mai|tuan sau|thang sau|million|thousand|billion|euro|euros|percent)\b|[万億円]", RegexOptions.Compiled);
    private static readonly Regex Act = new(@"\b(gui|chot|thong nhat|dong y|cam ket|bao gia|hop dong|poc|demo|pilot|trien khai|follow|lien he|email|tai lieu|ke hoach|lich|buoc tiep|next step|send|confirm|agree|quote|contract|proposal|ban giao|nghiem thu)\b", RegexOptions.Compiled);
    private static readonly Regex Tech = new(@"\b[A-Z][A-Za-z0-9]*[A-Z0-9][A-Za-z0-9]*\b|\b(API|SDK|AI|HSM|PKI|cloud|server|on-?prem|SaaS|license|module|camera|robot)\b", RegexOptions.Compiled);
    private static readonly Regex Neg = new(@"(^|[^a-z0-9])(not|no|never|cannot|cant|dont|doesnt|didnt|wont|isnt|arent|wasnt|werent|without|khong|chua|chang|ko)(?=$|[^a-z0-9])|n't|ない|ません|ぬ|[不没無未否]", RegexOptions.Compiled);
    private static readonly Regex Cjk = new(@"[\u3040-\u30ff\u4e00-\u9fff]", RegexOptions.Compiled);

    private static readonly string[] Ack =
    {
        "da", "vang", "u", "um", "uh", "uhm", "uh huh", "mhm", "mm", "mmm", "hmm", "a", "ah", "oh", "roi", "dung roi", "vay a", "the a", "cam on", "cam on anh", "cam on chi", "cam on em", "cam on ban", "xin chao", "alo", "nghe ro khong",
        "yeah", "yep", "right", "sure", "i see", "got it", "alright", "all right", "thank you", "thank you very much", "thanks", "nice", "great", "perfect", "good", "exactly", "indeed", "wow",
        "そうですね", "そうですか", "ええ", "うん", "なるほど", "そうそう", "へえ", "ありがとうございます", "了解です", "はいはい",
    };
    private static readonly string[] Yes = { "yes", "no", "nope", "ok", "okay", "oke", "co", "khong", "duoc", "chua", "はい", "いいえ", "いや" };
    private static readonly (string P, string Kind)[] Phrases =
        Ack.Select(p => (Fold(p), "ack")).Concat(Yes.Select(p => (Fold(p), "yes"))).OrderByDescending(x => x.Item1.Length).ToArray();

    private static readonly string[] Outro = new[]
    {
        "dang ky kenh", "dang ki kenh", "cam on cac ban da theo doi", "hay dang ky", "hay subscribe", "video hap dan", "lalaschool", "hen gap lai cac ban trong nhung video", "video tiep theo",
        "subscribe to my channel", "subscribe to the channel", "like and subscribe", "please subscribe", "thanks for watching", "thank you for watching", "amara.org", "subtitles by",
        "sous-titres realises par", "subtitulos realizados por", "продолжение следует", "подпишись на канал", "субтитры сделал", "субтитры подготовил", "구독과 좋아요", "시청해 주셔서 감사합니다",
        "ご視聴ありがとうございました", "チャンネル登録", "字幕由", "subscribe to our channel", "sous-titrage", "subtitulado por", "подписывайтесь на канал", "редактор субтитров", "구독 좋아요",
        "最後までご視聴", "请不吝点赞", "订阅我的频道", "theo doi va hen gap lai", "ghien mi go", "merci d'avoir regarde", "merci d avoir regarde", "abonnez-vous", "gracias por ver", "suscribete",
        "tekstitys:", "tekstitys lasse", "kiitos kun katsoit", "kiitos katsomisesta", "tekster af", "tak fordi du s", "undertekster af", "see you next time",
    }.Select(Fold).ToArray();

    private static readonly Dictionary<string, Regex> Scripts = new()
    {
        ["cyrillic"] = new(@"[\u0400-\u052f]"), ["hangul"] = new(@"[\uac00-\ud7af\u1100-\u11ff\u3130-\u318f]"),
        ["kana"] = new(@"[\u3040-\u30ff\u31f0-\u31ff\uff66-\uff9f]"), ["han"] = new(@"[\u3400-\u4dbf\u4e00-\u9fff]"),
        ["arabic"] = new(@"[\u0600-\u06ff]"), ["thai"] = new(@"[\u0e00-\u0e7f]"), ["hebrew"] = new(@"[\u0590-\u05ff]"),
        ["devanagari"] = new(@"[\u0900-\u097f]"), ["greek"] = new(@"[\u0370-\u03ff]"),
    };
    private static readonly Dictionary<string, string[]> LangScripts = new()
    {
        ["ja"] = new[] { "kana", "han" }, ["zh"] = new[] { "han" }, ["yue"] = new[] { "han" }, ["ko"] = new[] { "hangul", "han" },
        ["ru"] = new[] { "cyrillic" }, ["uk"] = new[] { "cyrillic" }, ["bg"] = new[] { "cyrillic" }, ["sr"] = new[] { "cyrillic" },
        ["el"] = new[] { "greek" }, ["ar"] = new[] { "arabic" }, ["fa"] = new[] { "arabic" }, ["th"] = new[] { "thai" },
        ["he"] = new[] { "hebrew" }, ["hi"] = new[] { "devanagari" },
    };

    private sealed record Said(HashSet<string> Tk, string Dg, double Sec, bool Neg, string Lab);

    private readonly Regex? _terms;
    private string[] _langs;
    private readonly int _heartbeat;
    private readonly List<HashSet<string>> _recent = new();
    private readonly List<Said> _said = new();
    private List<CoachLine> _held = new();

    /// <param name="terms">Folded terms from the brief (a line naming one wakes the coach).</param>
    /// <param name="langs">The meeting's languages: lines mostly in a script none of them writes are junk. Empty: no script test.</param>
    public CoachGate(IEnumerable<string>? terms = null, IEnumerable<string>? langs = null, int heartbeat = 6)
    {
        var t = terms?.Select(Fold).Where(x => x.Length >= 3).Distinct().ToArray() ?? Array.Empty<string>();
        _terms = t.Length > 0 ? new Regex(@"\b(" + string.Join("|", t.Select(Regex.Escape)) + @")\b") : null;
        _langs = langs?.ToArray() ?? Array.Empty<string>();
        _heartbeat = Math.Max(1, heartbeat);
    }

    /// <summary>The meeting's languages for the junk test (live text locked its language): used from the next line.</summary>
    public IReadOnlyList<string> Languages
    {
        get => _langs;
        set => _langs = value?.ToArray() ?? Array.Empty<string>();
    }

    /// <summary>The kit's fold: lower case, đ as d, accents removed (combining marks U+0300-U+036F only, so kana keep their marks).</summary>
    public static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (c < '\u0300' || c > '\u036f') sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static bool ScriptOk(string s, IReadOnlyCollection<string> langs)
    {
        if (langs.Count == 0) return true;
        var ok = new HashSet<string>(langs.SelectMany(l => LangScripts.TryGetValue(l, out var v) ? v : Array.Empty<string>()));
        var letters = s.Count(char.IsLetter);
        if (letters == 0) return true;
        var foreign = Scripts.Where(kv => !ok.Contains(kv.Key)).Sum(kv => kv.Value.Matches(s).Count);
        return foreign <= 0.2 * letters;
    }

    /// <summary>
    /// "ack" when the line is only acknowledgement fillers, "yes" when it has a yes/no answer among them, else null.
    /// Punctuation is stripped first like the kit's fillerKind, en and em dashes (U+2013, U+2014) included.
    /// </summary>
    /// <summary>The en and em dash (U+2013, U+2014), by code so the source holds no long dash.</summary>
    private static readonly string Dashes = new(new[] { (char)0x2013, (char)0x2014 });

    public static string? FillerKind(string folded)
    {
        var s = Regex.Replace(Regex.Replace(folded, @"[.,!?;:…、。！？~\-""'()" + Dashes + "]+", " "), @"\s+", " ").Trim();
        if (s.Length == 0) return "ack";
        int i = 0;
        var yes = false;
        while (i < s.Length)
        {
            if (s[i] == ' ') { i++; continue; }
            var at = i;
            var hit = Phrases.FirstOrDefault(p => string.CompareOrdinal(s, at, p.P, 0, p.P.Length) == 0 && at + p.P.Length <= s.Length
                && (at + p.P.Length == s.Length || s[at + p.P.Length] == ' ' || Cjk.IsMatch(p.P)));
            if (hit.P is null) return null;
            if (hit.Kind == "yes") yes = true;
            i += hit.P.Length;
        }
        return yes ? "yes" : "ack";
    }

    private static HashSet<string> Tokens(string s)
    {
        var f = Fold(s);
        var w = new HashSet<string>(Regex.Split(f, "[^a-z0-9]+").Where(x => x.Length > 2));
        var cj = Regex.Replace(f, @"[^\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uac00-\ud7af]", "");
        for (var i = 0; i + 1 < cj.Length; i++) w.Add(cj.Substring(i, 2));
        return w;
    }

    public GateResult Check(CoachLine line)
    {
        if (line.IsSys) return new GateResult(false, new[] { "sys" }) { Sys = true };
        if (line.Echo)
        {
            _held.Add(line);
            return new GateResult(false, new[] { "echo" });
        }
        var lab = line.Speaker;
        var text = line.Text.Replace("[?]", "").Trim();
        var f = Fold(text);
        if (text.Length == 0) return new GateResult(false, new[] { "filler" }) { Dropped = true };
        if (!ScriptOk(text, _langs) || Outro.Any(o => f.Contains(o, StringComparison.Ordinal)))
            return new GateResult(false, new[] { "junk" }) { Dropped = true };
        var fk = FillerKind(f);
        if (fk == "ack") return new GateResult(false, new[] { "filler" }) { Dropped = true };

        var tk = Tokens(text);
        var dg = string.Join(",", Regex.Matches(f, "[0-9]+").Select(m => m.Value));
        var sec = line.At.TotalSeconds;
        var neg = Neg.IsMatch(f);
        bool SameT(Said r) => Math.Abs(sec - r.Sec) <= (r.Lab != lab ? 1 : 120);
        if (tk.Count > 0 && _said.Any(r => SameT(r) && r.Dg == dg && r.Neg == neg && (double)tk.Count(r.Tk.Contains) / Math.Max(tk.Count, r.Tk.Count) > 0.8))
        {
            _held.Add(line);
            return new GateResult(false, new[] { "dup" });
        }
        _said.Add(new Said(tk, dg, sec, neg, lab));
        if (_said.Count > 20) _said.RemoveAt(0);

        var words = Regex.Matches(text, @"[\p{L}\p{N}]+").Count;
        var term = _terms?.IsMatch(f) == true;
        if (fk == "yes" || (words <= 3 && !Regex.IsMatch(text, "[0-9?？]") && !term && !Tech.IsMatch(text) && Cjk.Matches(text).Count < 6))
        {
            _held.Add(line);
            return new GateResult(false, new[] { fk == "yes" ? "yesno" : "short" });
        }
        var reasons = new List<string>();
        if (Q.IsMatch(f)) reasons.Add("q");
        if (Num.IsMatch(f)) reasons.Add("num");
        if (Act.IsMatch(f)) reasons.Add("act");
        if (Tech.IsMatch(text)) reasons.Add("tech");
        if (term) reasons.Add("term");
        var win = new HashSet<string>(_recent.SelectMany(x => x));
        var ov = tk.Count > 0 ? (double)tk.Count(win.Contains) / tk.Count : 1;
        if (_recent.Count >= 4 && ov < 0.12 && text.Length > 60) reasons.Add("topic");
        _recent.Add(tk);
        if (_recent.Count > 6) _recent.RemoveAt(0);
        if (reasons.Count > 0) return Finish(reasons.ToArray(), line);
        _held.Add(line);
        if (_held.Count >= _heartbeat)
        {
            _held.RemoveAt(_held.Count - 1);
            return Finish(new[] { "heartbeat" }, line);
        }
        return new GateResult(false, new[] { "hold" });
    }

    private GateResult Finish(string[] reasons, CoachLine trigger)
    {
        var bundle = _held;
        _held = new List<CoachLine>();
        return new GateResult(true, reasons) { Bundle = bundle, Trigger = trigger };
    }
}

/// <summary>At most one CUE per gap of meeting time (the kit: 180 s), so cues stay rare enough to be read.</summary>
public sealed class CueThrottle
{
    private readonly TimeSpan _gap;
    private TimeSpan? _last;

    public CueThrottle(TimeSpan gap) => _gap = gap;

    public bool TryTake(TimeSpan at)
    {
        if (_last is { } l && at >= l && at - l < _gap) return false;
        _last = at;
        return true;
    }
}

/// <summary>Shows a CUE outside the window. The app's sink is a quiet tray balloon that never takes focus.</summary>
public interface ICoachToast
{
    void Show(string title, string text);
}

/// <summary>A model's answer and its tokens: as the tool reported them, or chars / 4 when it reported none (<see cref="Estimated"/>).</summary>
public sealed record CoachReply(string Text, long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0, bool Estimated = false);

public interface ICoachProvider
{
    /// <summary>For the panel and coach.json: "Claude Code", "Codex", "Gemini CLI", the local server's host.</summary>
    string Name { get; }
    string Model { get; }
    /// <summary>True when nothing leaves this PC (local endpoint, fake).</summary>
    bool IsLocal { get; }
    Task<CoachReply> CompleteAsync(string system, string user, CancellationToken ct);
}

/// <summary>A provider failure with a short message that is safe to show and log (never the key).</summary>
public sealed class CoachProviderException(string message) : Exception(message);

public static class CoachProviders
{
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>
    /// The provider for these settings; throws <see cref="CoachProviderException"/> with what the user must do. RecBox
    /// never calls a cloud API itself: the AI tools installed on this PC (their own sign-in), or a model server on this PC.
    /// </summary>
    public static ICoachProvider Create(CoachSettings s, HttpClient? http = null)
    {
        http ??= SharedHttp.Value;
        switch (s.Provider)
        {
            case CoachProviderKind.ClaudeCli:
            case CoachProviderKind.Anthropic: // a 1.3.0 preview setting: Claude Code instead of a key
                var exe = ClaudeCliProvider.Find() ?? throw new CoachProviderException(Loc.F("cli_missing", Loc.T("ai_claude")));
                return new ClaudeCliProvider(exe, s.Model ?? "");
            case CoachProviderKind.CodexCli:
                var codex = CodexCliProvider.Find() ?? throw new CoachProviderException(Loc.F("cli_missing", Loc.T("ai_codex")));
                return new CodexCliProvider(codex, s.CodexModel ?? "");
            case CoachProviderKind.GeminiCli:
                var gemini = GeminiCliProvider.Find() ?? throw new CoachProviderException(Loc.F("cli_missing", Loc.T("ai_gemini")));
                return new GeminiCliProvider(gemini, s.GeminiModel ?? "");
            case CoachProviderKind.OpenAiCompatible:
                if (!Uri.TryCreate(s.Endpoint?.Trim(), UriKind.Absolute, out _)) throw new CoachProviderException(Loc.T("endpoint_missing"));
                if (!CoachPrivacy.IsLocalEndpoint(s.Endpoint)) throw new CoachProviderException(Loc.T("endpoint_not_local"));
                return new OpenAiCompatibleProvider(http, s.Endpoint!.Trim(), null, string.IsNullOrWhiteSpace(s.EndpointModel) ? "llama3.1" : s.EndpointModel.Trim());
            default:
                return new FakeCoachProvider();
        }
    }

    /// <summary>True when the AI tool is installed on this PC (a local model server is never checked: it may start later).</summary>
    public static bool IsInstalled(CoachProviderKind kind) => kind switch
    {
        CoachProviderKind.ClaudeCli or CoachProviderKind.Anthropic => ClaudeCliProvider.Find() is not null,
        CoachProviderKind.CodexCli => CodexCliProvider.Find() is not null,
        CoachProviderKind.GeminiCli => GeminiCliProvider.Find() is not null,
        _ => true,
    };

    /// <summary>1.3.0 previews kept API keys in coach-keys.dat; nothing uses a key any more, so the file goes.</summary>
    public static void ForgetOldKeys()
    {
        try
        {
            var f = Path.Combine(AppPaths.Home, "coach-keys.dat");
            if (!File.Exists(f)) return;
            File.Delete(f);
            Log.Info("removed coach-keys.dat (RecBox no longer keeps API keys)");
        }
        catch (Exception ex)
        {
            Log.Warn($"coach-keys.dat not removed: {ex.Message}");
        }
    }

    internal static string ErrorText(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : e.ToString();
        }
        catch (JsonException) { }
        return body;
    }

    /// <summary>"Codex answered nothing (exit 1): ..." in the interface language; the tool's own output stays as it is (keys masked).</summary>
    internal static CoachProviderException NoAnswer(string tool, int exitCode, string stdout, string stderr) =>
        new(Loc.F("cli_no_answer", tool, exitCode, Safe(stderr.Length > 0 ? stderr : stdout, null, 120)));

    /// <summary>A program on PATH, in ~\.local\bin or the npm folder (%AppData%\npm): an exe first, then the npm .cmd shim.</summary>
    internal static string? FindTool(string envVar, string name)
    {
        if (Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } bin && File.Exists(bin)) return bin;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var file in new[] { name + ".exe", name + ".cmd" })
            foreach (var d in dirs)
            {
                try
                {
                    var p = Path.Combine(d.Trim('"'), file);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    /// <summary>A message with any secret replaced, cut short for the panel.</summary>
    public static string Safe(string message, string? secret, int max = 160)
    {
        if (!string.IsNullOrEmpty(secret)) message = message.Replace(secret, "***", StringComparison.Ordinal);
        message = Regex.Replace(message, @"(sk-[A-Za-z0-9_\-]{6})[A-Za-z0-9_\-]+", "$1***");
        message = Regex.Replace(message, @"\s+", " ").Trim();
        return message.Length > max ? message[..max] + "..." : message;
    }

    internal static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}

/// <summary>
/// A Windows Job object that kills every process in it when its last handle closes (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE).
/// RecBox puts each AI tool it starts in one, so the tool and everything it started die with RecBox, even when RecBox is
/// killed or crashes and no code of ours runs. Child processes join the job of their parent on their own.
/// </summary>
public sealed class ChildJob : IDisposable
{
    private IntPtr _handle;
    private readonly object _gate = new();

    public ChildJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException($"CreateJobObject failed ({Marshal.GetLastWin32Error()})");
        var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimits>();
        var mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, mem, false);
            if (!SetInformationJobObject(_handle, ExtendedLimitInformation, mem, (uint)size))
            {
                var err = Marshal.GetLastWin32Error();
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
                throw new InvalidOperationException($"SetInformationJobObject failed ({err})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    /// <summary>Puts a started process in the job; false when it already ended or the job is closed.</summary>
    public bool Assign(Process p)
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return false;
            try { return AssignProcessToJobObject(_handle, p.Handle); }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
    }

    /// <summary>Closes the job: every process still in it is killed (what happens at host exit).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Runs an AI tool installed on this PC headless: no window (CreateNoWindow), stdin / stdout / stderr in UTF-8, in a
/// <see cref="ChildJob"/> so it dies with RecBox, and killed with its whole tree the moment the call is cancelled (Stop,
/// pause, timeout). The temp files it is given are named recbox-coach-&lt;RecBox pid&gt;-&lt;guid&gt;.md, deleted after the call,
/// at exit (<see cref="KillAll"/>), and at the next start when an exit left one behind (<see cref="SweepStale"/>).
/// </summary>
public static class CliRunner
{
    public const string TempPrefix = "recbox-coach-";
    private static readonly object Gate = new();
    private static readonly HashSet<Process> Running = new();
    private static readonly HashSet<string> Temps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<ChildJob?> Job = new(() =>
    {
        try { return new ChildJob(); }
        catch (Exception ex)
        {
            Log.Warn($"no job object for AI tools ({ex.Message}); they are still killed on Stop and at exit");
            return null;
        }
    });

    /// <summary>Where the temp files go (the user's temp folder; tests point it elsewhere).</summary>
    public static string TempDir { get; set; } = Path.GetTempPath();

    /// <summary>Test hook: each process right after it started and joined the job.</summary>
    public static Action<Process>? Started { get; set; }

    public sealed record Result(string Stdout, string Stderr, int ExitCode);

    public static int RunningCount
    {
        get { lock (Gate) return Running.Count; }
    }

    /// <summary>Writes text to a new temp file of this run; delete it with <see cref="DeleteTemp"/>.</summary>
    public static string NewTempFile(string text)
    {
        Directory.CreateDirectory(TempDir);
        var f = Path.Combine(TempDir, $"{TempPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}.md");
        lock (Gate) Temps.Add(f);
        File.WriteAllText(f, text, new UTF8Encoding(false));
        return f;
    }

    public static void DeleteTemp(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) { Log.Warn($"temp file not deleted ({ex.GetType().Name}); removed at the next start"); }
        lock (Gate) Temps.Remove(file);
    }

    /// <summary>An empty folder to run a tool in, so an agent CLI has no project files around it.</summary>
    public static string EmptyDir()
    {
        var d = Path.Combine(TempDir, TempPrefix + "work");
        Directory.CreateDirectory(d);
        return d;
    }

    internal static string Quote(string a) => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    /// <summary>The command line: an exe as is; an npm .cmd / .bat shim through cmd.exe /d /s /c.</summary>
    public static ProcessStartInfo StartInfo(string exe, IReadOnlyList<string> args, string? workDir = null)
    {
        var line = string.Join(" ", args.Select(Quote));
        var shim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = shim ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe,
            Arguments = shim ? $"/d /s /c \"{Quote(exe)} {line}\"" : line,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        return psi;
    }

    /// <summary>One run: stdin written then closed, output read to the end; cancelling kills the whole tree at once.</summary>
    public static async Task<Result> RunAsync(string exe, IReadOnlyList<string> args, string stdin, string name, CancellationToken ct, string? workDir = null)
    {
        ct.ThrowIfCancellationRequested();
        var p = new Process { StartInfo = StartInfo(exe, args, workDir) };
        try
        {
            if (!p.Start()) throw new CoachProviderException(Loc.F("cli_not_started", name));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            p.Dispose();
            throw new CoachProviderException(Loc.F("cli_not_started", name) + " " + ex.Message);
        }
        // Edge: a child the tool starts in the moment before this line is outside the job (Kill on Stop still gets it).
        Job.Value?.Assign(p);
        lock (Gate) Running.Add(p);
        try
        {
            Started?.Invoke(p);
            using var reg = ct.Register(() => KillTree(p));
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            try
            {
                await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
                p.StandardInput.Close();
            }
            catch (IOException) { } // the tool ended before reading its input: its exit code and stderr say why
            await p.WaitForExitAsync(ct);
            var stdout = await outTask;
            var stderr = await errTask;
            return new Result(stdout, stderr, p.ExitCode);
        }
        catch (OperationCanceledException)
        {
            KillTree(p);
            throw;
        }
        finally
        {
            lock (Gate) Running.Remove(p);
            p.Dispose();
        }
    }

    private static void KillTree(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception) { } // already gone
    }

    /// <summary>At exit: kills every tool still running and deletes this run's temp files.</summary>
    public static void KillAll()
    {
        Process[] procs;
        string[] files;
        lock (Gate)
        {
            procs = Running.ToArray();
            files = Temps.ToArray();
        }
        foreach (var p in procs) KillTree(p);
        foreach (var f in files)
        {
            try { File.Delete(f); } catch (Exception) { }
        }
        lock (Gate) Temps.Clear();
    }

    /// <summary>
    /// At start: deletes temp files an earlier RecBox left (it was killed mid-call): a file of a RecBox that is no longer
    /// running, or older than a day, or one named the 1.3.0 preview way (no pid) and older than 10 minutes.
    /// </summary>
    public static int SweepStale(string? dir = null)
    {
        dir ??= TempDir;
        var removed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in Directory.EnumerateFiles(dir, TempPrefix + "*.md"))
            {
                var name = Path.GetFileName(f);
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(f);
                var stale = false;
                var m = Regex.Match(name, @"^recbox-coach-(\d+)-[0-9a-f]{32}\.md$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var pid = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    stale = pid != Environment.ProcessId && (!Alive(pid) || age > TimeSpan.FromDays(1));
                }
                else if (Regex.IsMatch(name, @"^recbox-coach-[0-9a-f]{32}\.md$", RegexOptions.IgnoreCase))
                    stale = age > TimeSpan.FromMinutes(10);
                if (!stale) continue;
                try
                {
                    File.Delete(f);
                    removed++;
                }
                catch (Exception) { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"temp sweep skipped ({ex.GetType().Name})");
        }
        if (removed > 0) Log.Info($"removed {removed} coach temp file(s) left by an earlier run");
        return removed;
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The tokens to count when a tool reported none: chars / 4, marked as an estimate.</summary>
    public static CoachReply Estimate(string text, string system, string user) =>
        new(text, UsageEstimate.FromChars(system.Length + user.Length), UsageEstimate.FromChars(text.Length), Estimated: true);
}

/// <summary>
/// Claude Code run headless, like the kit's listener: one print-mode call per wake, no tools, no settings, no MCP, no
/// session saved. The system prompt goes in a temp file, the wake on stdin; the JSON result carries the usage.
/// Flags checked against claude --help on this PC.
/// </summary>
public sealed class ClaudeCliProvider : ICoachProvider
{
    private readonly string _exe;

    public ClaudeCliProvider(string exe, string model)
    {
        _exe = exe;
        Model = model ?? "";
    }

    public string Name => "Claude Code";
    public string Model { get; }
    public bool IsLocal => false;

    /// <summary>CLAUDE_BIN, claude.exe on PATH, in ~\.local\bin or %AppData%\npm, else the npm claude.cmd shim. Null when not installed.</summary>
    public static string? Find() => CoachProviders.FindTool("CLAUDE_BIN", "claude");

    public static IReadOnlyList<string> Arguments(string model, string systemFile)
    {
        var a = new List<string> { "-p", "--output-format", "json" };
        if (!string.IsNullOrWhiteSpace(model)) a.AddRange(new[] { "--model", model.Trim() });
        a.AddRange(new[]
        {
            "--effort", "low", "--system-prompt-file", systemFile,
            "--tools", "", "--setting-sources", "", "--strict-mcp-config", "--no-session-persistence",
        });
        return a;
    }

    public async Task<CoachReply> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var sysFile = CliRunner.NewTempFile(system);
        try
        {
            var r = await CliRunner.RunAsync(_exe, Arguments(Model, sysFile), user, Name, ct);
            var reply = Parse(r.Stdout, r.Stderr, r.ExitCode);
            return reply.InputTokens + reply.OutputTokens + reply.CacheReadTokens + reply.CacheWriteTokens > 0 ? reply : CliRunner.Estimate(reply.Text, system, user);
        }
        finally
        {
            CliRunner.DeleteTemp(sysFile);
        }
    }

    /// <summary>
    /// Reads the answer in every shape Claude Code prints for --output-format json: one result object (older builds), one
    /// line holding an array of events whose "result" element carries the answer (2.x), one event object per line, or an
    /// object printed over several lines. The element with type "result" wins; else the last object.
    /// </summary>
    internal static CoachReply Parse(string stdout, string stderr, int exitCode)
    {
        var docs = new List<JsonDocument>();
        try
        {
            void TryAdd(string text)
            {
                try { docs.Add(JsonDocument.Parse(text)); }
                catch (JsonException) { }
            }
            var whole = stdout.Trim();
            if (whole.StartsWith('{') || whole.StartsWith('[')) TryAdd(whole);
            if (docs.Count == 0)
                foreach (var l in stdout.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{') || l.StartsWith('[')))
                    TryAdd(l);
            var objects = docs.SelectMany(d => d.RootElement.ValueKind == JsonValueKind.Array
                    ? d.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
                    : d.RootElement.ValueKind == JsonValueKind.Object ? new[] { d.RootElement } : Array.Empty<JsonElement>())
                .ToList();
            if (objects.Count == 0)
                throw CoachProviders.NoAnswer("Claude Code", exitCode, stdout, stderr);
            var result = objects.LastOrDefault(e => e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "result");
            return Read(result.ValueKind == JsonValueKind.Object ? result : objects[^1], stderr, exitCode);
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }

    private static CoachReply Read(JsonElement r, string stderr, int exitCode)
    {
        var text = r.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String ? res.GetString() ?? "" : "";
        var isError = r.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        if (isError || exitCode != 0) throw new CoachProviderException($"Claude Code: {CoachProviders.Safe(text.Length > 0 ? text : stderr, null, 120)}");
        var u = r.TryGetProperty("usage", out var uu) ? uu : default;
        return new CoachReply(text, CoachProviders.Long(u, "input_tokens"), CoachProviders.Long(u, "output_tokens"),
            CoachProviders.Long(u, "cache_read_input_tokens"), CoachProviders.Long(u, "cache_creation_input_tokens"));
    }
}

/// <summary>
/// OpenAI's Codex CLI run headless: codex exec --json, read-only sandbox, in an empty folder, the prompt on stdin ("-").
/// Codex is not installed on the build PC: the flags and the JSONL events (item.completed agent_message, turn.completed
/// usage, error / turn.failed) follow Codex's documentation and are tested with a fake CLI only.
/// </summary>
public sealed class CodexCliProvider : ICoachProvider
{
    private readonly string _exe;

    public CodexCliProvider(string exe, string model)
    {
        _exe = exe;
        Model = model ?? "";
    }

    public string Name => "Codex";
    public string Model { get; }
    public bool IsLocal => false;

    /// <summary>CODEX_BIN, codex.exe on PATH, in ~\.local\bin or %AppData%\npm, else the npm codex.cmd shim.</summary>
    public static string? Find() => CoachProviders.FindTool("CODEX_BIN", "codex");

    public static IReadOnlyList<string> Arguments(string model)
    {
        var a = new List<string> { "exec", "--json", "--skip-git-repo-check", "--sandbox", "read-only" };
        if (!string.IsNullOrWhiteSpace(model)) a.AddRange(new[] { "--model", model.Trim() });
        a.Add("-");
        return a;
    }

    /// <summary>Codex takes one prompt: the instructions, then the wake.</summary>
    public static string Prompt(string system, string user) => system + "\n\n---\n\n" + user;

    public async Task<CoachReply> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var r = await CliRunner.RunAsync(_exe, Arguments(Model), Prompt(system, user), Name, ct, CliRunner.EmptyDir());
        var reply = Parse(r.Stdout, r.Stderr, r.ExitCode);
        return reply.InputTokens + reply.OutputTokens > 0 ? reply : CliRunner.Estimate(reply.Text, system, user);
    }

    internal static CoachReply Parse(string stdout, string stderr, int exitCode)
    {
        string? text = null;
        string? error = null;
        long input = 0, cached = 0, output = 0;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{')) continue;
            try
            {
                using var d = JsonDocument.Parse(line);
                var e = d.RootElement;
                var type = Str(e, "type");
                if (type == "item.completed" && e.TryGetProperty("item", out var item))
                {
                    var kind = Str(item, "type") ?? Str(item, "item_type");
                    if (kind is "agent_message" or "assistant_message") text = Str(item, "text") ?? text;
                }
                else if (type == "turn.completed" && e.TryGetProperty("usage", out var u))
                {
                    input = CoachProviders.Long(u, "input_tokens");
                    cached = CoachProviders.Long(u, "cached_input_tokens");
                    output = CoachProviders.Long(u, "output_tokens");
                }
                else if (type == "error") error = Str(e, "message") ?? line;
                else if (type == "turn.failed")
                    error = e.TryGetProperty("error", out var fe) && fe.ValueKind == JsonValueKind.Object ? Str(fe, "message") ?? line : line;
                else if (e.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.Object)
                {
                    // Older Codex builds: {"id":..,"msg":{"type":"agent_message","message":..}} and token_count events.
                    var mt = Str(msg, "type");
                    if (mt == "agent_message") text = Str(msg, "message") ?? text;
                    else if (mt == "error") error = Str(msg, "message") ?? line;
                    else if (mt == "token_count" && msg.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                             && info.TryGetProperty("total_token_usage", out var tu))
                    {
                        input = CoachProviders.Long(tu, "input_tokens");
                        cached = CoachProviders.Long(tu, "cached_input_tokens");
                        output = CoachProviders.Long(tu, "output_tokens");
                    }
                }
            }
            catch (JsonException) { }
        }
        if (error is not null && string.IsNullOrEmpty(text)) throw new CoachProviderException($"Codex: {CoachProviders.Safe(error, null, 120)}");
        if (text is null)
            throw CoachProviders.NoAnswer("Codex", exitCode, stdout, stderr);
        // OpenAI counts cached tokens inside input_tokens: split them so "in" and "cached" do not overlap.
        return new CoachReply(text, Math.Max(0, input - cached), output, cached);
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Google's Gemini CLI run headless: gemini -p with JSON output; the instructions and the wake go on stdin (Gemini CLI
/// puts its stdin before the -p text). Gemini CLI is not installed on the build PC: the flags and the JSON shape
/// (response, stats.models.*.tokens, error.message) follow its documentation and are tested with a fake CLI only.
/// </summary>
public sealed class GeminiCliProvider : ICoachProvider
{
    private readonly string _exe;
    public const string Instruction = "Follow the instructions above and answer only in the format they ask for.";

    public GeminiCliProvider(string exe, string model)
    {
        _exe = exe;
        Model = model ?? "";
    }

    public string Name => "Gemini CLI";
    public string Model { get; }
    public bool IsLocal => false;

    /// <summary>GEMINI_BIN, gemini.exe on PATH, in ~\.local\bin or %AppData%\npm, else the npm gemini.cmd shim.</summary>
    public static string? Find() => CoachProviders.FindTool("GEMINI_BIN", "gemini");

    public static IReadOnlyList<string> Arguments(string model)
    {
        var a = new List<string> { "-p", Instruction, "--output-format", "json" };
        if (!string.IsNullOrWhiteSpace(model)) a.AddRange(new[] { "-m", model.Trim() });
        return a;
    }

    public async Task<CoachReply> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var r = await CliRunner.RunAsync(_exe, Arguments(Model), CodexCliProvider.Prompt(system, user), Name, ct, CliRunner.EmptyDir());
        var reply = Parse(r.Stdout, r.Stderr, r.ExitCode);
        return reply.InputTokens + reply.OutputTokens > 0 ? reply : CliRunner.Estimate(reply.Text, system, user);
    }

    internal static CoachReply Parse(string stdout, string stderr, int exitCode)
    {
        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw CoachProviders.NoAnswer("Gemini CLI", exitCode, stdout, stderr);
        try
        {
            using var d = JsonDocument.Parse(stdout[start..(end + 1)]);
            var r = d.RootElement;
            if (r.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                throw new CoachProviderException($"Gemini CLI: {CoachProviders.Safe(CodexCliProvider.Str(err, "message") ?? err.ToString(), null, 120)}");
            var text = CodexCliProvider.Str(r, "response")
                ?? throw CoachProviders.NoAnswer("Gemini CLI", exitCode, stdout, stderr);
            long prompt = 0, cached = 0, output = 0;
            if (r.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object
                && stats.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
                foreach (var m in models.EnumerateObject())
                    if (m.Value.ValueKind == JsonValueKind.Object && m.Value.TryGetProperty("tokens", out var t))
                    {
                        prompt += CoachProviders.Long(t, "prompt");
                        cached += CoachProviders.Long(t, "cached");
                        output += CoachProviders.Long(t, "candidates");
                    }
            return new CoachReply(text, Math.Max(0, prompt - cached), output, cached);
        }
        catch (JsonException)
        {
            throw CoachProviders.NoAnswer("Gemini CLI", exitCode, stdout, stderr);
        }
    }
}

/// <summary>An OpenAI-compatible model server on this PC: Ollama or LM Studio (only local addresses are used).</summary>
public sealed class OpenAiCompatibleProvider : ICoachProvider
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string? _key;

    public OpenAiCompatibleProvider(HttpClient http, string baseUrl, string? key, string model)
    {
        _http = http;
        _base = baseUrl.TrimEnd('/');
        _key = string.IsNullOrWhiteSpace(key) ? null : key;
        Model = model;
    }

    public string Name => Uri.TryCreate(_base, UriKind.Absolute, out var u) ? u.Host : _base;
    public string Model { get; }
    public bool IsLocal => CoachPrivacy.IsLocalEndpoint(_base);

    public async Task<CoachReply> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = Model,
            max_tokens = 800,
            temperature = 0.2,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/chat/completions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (_key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new CoachProviderException($"{Name} {(int)resp.StatusCode}: {CoachProviders.Safe(CoachProviders.ErrorText(text), _key, 120)}");
        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;
        var answer = "";
        if (r.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0
            && ch[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
            answer = c.GetString() ?? "";
        var u = r.TryGetProperty("usage", out var uu) ? uu : default;
        var reply = new CoachReply(answer, CoachProviders.Long(u, "prompt_tokens"), CoachProviders.Long(u, "completion_tokens"));
        return reply.InputTokens + reply.OutputTokens > 0 ? reply : CliRunner.Estimate(answer, system, user);
    }
}

/// <summary>A rule-based stand-in for the replay and the screenshots: never sends anything, costs nothing.</summary>
public sealed class FakeCoachProvider : ICoachProvider
{
    public string Name => Loc.T("ai_fake");
    public string Model => "rules";
    public bool IsLocal => true;
    public int Calls { get; private set; }

    public Task<CoachReply> CompleteAsync(string system, string user, CancellationToken ct)
    {
        Calls++;
        var marker = user.LastIndexOf(CoachPrompt.NewHeader, StringComparison.Ordinal);
        var fresh = marker >= 0 ? user[(marker + CoachPrompt.NewHeader.Length)..] : user;
        var lines = fresh.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('[')).ToList();
        var outLines = new List<string>();
        foreach (var l in lines)
        {
            var text = Regex.Replace(l, @"^(\[[^\]]*\]\s*)+", "").Trim();
            if (text.Length == 0) continue;
            if (text.Contains('?')) outLines.Add($"QA: + | {Cut(text, 90)} |");
            else if (Regex.IsMatch(text, @"\d")) outLines.Add($"LOG: {Cut(text, 120)}");
        }
        if (outLines.Any(o => o.StartsWith("QA:", StringComparison.Ordinal)))
            outLines.Add($"CUE: They asked: {Cut(Regex.Replace(lines.Last(), @"^(\[[^\]]*\]\s*)+", ""), 80)} - answer it or say when you will.");
        if (outLines.Count == 0) outLines.Add("SKIP");
        var reply = string.Join("\n", outLines);
        return Task.FromResult(new CoachReply(reply, (system.Length + user.Length) / 4, reply.Length / 4));
    }

    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
}

/// <summary>What the user set for this meeting, ready for the prompt.</summary>
public sealed class CoachSetup
{
    public string Goal { get; init; } = "";
    public string Brief { get; init; } = "";
    public string Role { get; init; } = "";
    public string StyleName { get; init; } = CoachStyles.All[0].Name;
    public string StyleInstruction { get; init; } = CoachStyles.All[0].Instruction;
    public string Instructions { get; init; } = "";
    public string OutputLanguage { get; init; } = "same";
    /// <summary>The user's name and the other side's names from the brief ("Me: Lan", "Them: Mr Sato, Ms Ito"); null: You / Others.</summary>
    public string? YouName { get; init; }
    public string? OthersName { get; init; }
    public IReadOnlyList<string> Terms { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Questions { get; init; } = Array.Empty<string>();

    public static CoachSetup From(CoachSettings s)
    {
        var style = CoachStyles.Get(s.Style);
        var names = BriefNames.Parse(s.Brief);
        return new CoachSetup
        {
            Goal = s.Goal.Trim(),
            Brief = s.Brief.Trim(),
            Role = s.Role.Trim(),
            StyleName = style.Name,
            StyleInstruction = style.Instruction,
            Instructions = s.Instructions.Trim(),
            OutputLanguage = s.OutputLanguage,
            YouName = names.You,
            OthersName = names.Others,
            Terms = BriefNames.Terms(s.Goal + "\n" + s.Brief),
            Questions = BriefNames.Questions(s.Brief),
        };
    }

    public string Label(CoachLine l) => l.IsSys ? "SYS" : l.Echo ? "Me?echo" : l.Speaker == "You" ? YouName ?? "Me" : OthersName ?? "Remote";
}

public sealed class CoachEntry
{
    public double At { get; set; }
    public string Time { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class CoachQuestion
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    /// <summary>True for a question from the brief; false for one asked in the meeting.</summary>
    public bool FromBrief { get; set; }
    public double? AnsweredAt { get; set; }
}

public sealed class CoachNextStep
{
    public double At { get; set; }
    public string Time { get; set; } = "";
    public string Action { get; set; } = "";
    public string Who { get; set; } = "";
    public string When { get; set; } = "";
    /// <summary>For the panel: "Who: Lan. When: today".</summary>
    [JsonIgnore]
    public string Detail => string.Join(". ", new[] { Who.Length > 0 ? Loc.F("step_who", Who) : "", When.Length > 0 ? Loc.F("step_when", When) : "" }.Where(x => x.Length > 0));
}

/// <summary>Everything the coach wrote so far, plus its usage. A copy: safe to hold on another thread.</summary>
public sealed class CoachSnapshot
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public bool Local { get; set; }
    public string Goal { get; set; } = "";
    public string Role { get; set; } = "";
    public string Style { get; set; } = "";
    public List<CoachEntry> Cues { get; set; } = new();
    public List<CoachQuestion> Questions { get; set; } = new();
    public List<CoachNextStep> NextSteps { get; set; } = new();
    /// <summary>LOG lines, oldest first.</summary>
    public List<CoachEntry> Notes { get; set; } = new();
    /// <summary>ESCALATE lines and RecBox notes ([SYS]): what needs the user's attention.</summary>
    public List<CoachEntry> Alerts { get; set; } = new();
    public int Calls { get; set; }
    public int Failures { get; set; }
    public int ThrottledCues { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    /// <summary>True when a tool reported no usage for some call and RecBox counted chars / 4 for it.</summary>
    public bool TokensEstimated { get; set; }
    public string Status { get; set; } = "";

    private static string N(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>For the panel, in the interface language: calls and tokens (no prices).</summary>
    [JsonIgnore]
    public string UsageText
    {
        get
        {
            if (Local) return Loc.F("usage_local", Calls);
            var s = Loc.F("usage_tokens", Calls, N(InputTokens + CacheWriteTokens), N(OutputTokens));
            if (CacheReadTokens > 0) s += Loc.F("usage_cached", N(CacheReadTokens));
            return TokensEstimated ? s + " " + Loc.T("usage_estimated") : s;
        }
    }

    /// <summary>The same in English, for coach.md.</summary>
    [JsonIgnore]
    public string UsageLine => Local
        ? $"{Calls} call(s), runs on this PC"
        : $"{Calls} call(s), {N(InputTokens + CacheWriteTokens)} tokens in, {N(OutputTokens)} out"
          + (CacheReadTokens > 0 ? $", {N(CacheReadTokens)} cached" : "") + (TokensEstimated ? " (estimated: chars / 4)" : "");
}

/// <summary>The prompt: the kit's LIVE-PROMPT rules for an app, the brief, the coach's state, and the transcript.</summary>
public static class CoachPrompt
{
    public const string NewHeader = "# NEW TRANSCRIPT";
    public const int MaxBriefChars = 12000;
    public const int MaxRecentChars = 6000;
    public const int MaxNewChars = 5000;

    public static string System(CoachSetup s)
    {
        var lang = s.OutputLanguage is "same" or "" ? "the main language of the meeting" : CoachLanguages.Name(s.OutputLanguage);
        var sb = new StringBuilder();
        sb.AppendLine("You coach the user live in a meeting through an automatic speech-recognition (ASR) transcript. The user reads your lines while people talk, so every line must be useful within seconds. You have no tools: the app applies your reply lines to the user's meeting panel.");
        sb.AppendLine();
        sb.AppendLine("## Output: zero or more of these lines, nothing else");
        sb.AppendLine("- `QA: <row id> | <short answer>`: the meeting answers (fully or partly) a question in the questions table. Partial answers start with `(partly)`. This is the most valuable output.");
        sb.AppendLine("- `QA: + | <question asked (who asked)> | <answer so far, or empty>`: someone asks a substantive question that is not in the table yet.");
        sb.AppendLine("- `NEXT: <action> | <who> | <deadline or empty>`: a next step, promise or deliverable someone commits to.");
        sb.AppendLine("- `LOG: <one line>`: a new fact, number, objection, decision or topic change that serves the goal and is not already a QA or NEXT line.");
        sb.AppendLine("- `CUE: <what the user should say or ask now, and why now>`: only when the user should act in the next minute (a question is put to the user's side, a goal question can be asked now, the meeting wraps up before a goal question is answered, a figure contradicts the brief). At most 2 short sentences. Rare: at most one every ~3 minutes, never one that repeats an earlier cue.");
        sb.AppendLine("- `ESCALATE: <why>`: something needs the user's attention beyond one line (a claim to check, a contradiction with the brief). Rare.");
        sb.AppendLine("- `SKIP`: nothing new (greetings, fillers, repetition, a point already written).");
        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine("- ASR is noisy: correct obvious mishearings using the brief's names and terms, and mark the correction ('X' = Y). If unsure, keep the ASR words in quotes and add [to confirm].");
        sb.AppendLine($"- Labels: [{s.OthersName ?? "Remote"}] = the other side (system audio), [{s.YouName ?? "Me"}] = the user's microphone, [Me?echo] = a mic line that repeats the other side at the same moment (the mic hearing the speakers): treat it as the other side's words, never as the user's answer. [SYS] = a RecBox note about the capture.");
        sb.AppendLine("- A line or piece that starts with [?] is low-confidence ASR: use it only to confirm what clear lines say; never take a number, name, question or commitment from it alone, never a CUE.");
        sb.AppendLine("- Numbers, dates and deadlines heard live are unconfirmed unless said twice or confirmed. A question or a hypothetical is not a fact.");
        sb.AppendLine("- Never invent a commitment, a price or a deployment that was not said. Never suggest ending the meeting.");
        sb.AppendLine("- Short: a line is at most ~30 words. Do not repeat what the state below already holds.");
        sb.AppendLine($"- Write every line in {lang}; keep names and technical terms as they are.");
        sb.AppendLine();
        sb.AppendLine("# MEETING BRIEF");
        if (s.Goal.Length > 0) sb.AppendLine($"Goal: {s.Goal}");
        if (s.Role.Length > 0) sb.AppendLine($"The user's role: {s.Role}");
        if (s.YouName is not null || s.OthersName is not null) sb.AppendLine($"Names: the user = {s.YouName ?? "Me"}; the other side = {s.OthersName ?? "Remote"}.");
        sb.AppendLine($"Coaching style: {s.StyleName}. {s.StyleInstruction}");
        if (s.Instructions.Length > 0) sb.AppendLine($"The user's instructions: {s.Instructions}");
        if (s.Brief.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(s.Brief.Length > MaxBriefChars ? s.Brief[..MaxBriefChars] + "\n(brief cut here)" : s.Brief);
        }
        return sb.ToString();
    }

    public const int MaxQuestions = 40;
    public const int MaxAnswerChars = 200;

    /// <summary>
    /// The questions the model sees: every open one first, then the most recently answered, at most 40, in table order
    /// (a long meeting adds rows past 40: the newest must still reach the model, the oldest answered ones may not).
    /// </summary>
    public static IReadOnlyList<CoachQuestion> PromptQuestions(IReadOnlyList<CoachQuestion> all)
    {
        if (all.Count <= MaxQuestions) return all;
        var pick = all.Select((q, i) => (q, i)).Where(x => x.q.Answer.Length == 0).Take(MaxQuestions).ToList();
        pick.AddRange(all.Select((q, i) => (q, i)).Where(x => x.q.Answer.Length > 0)
            .OrderByDescending(x => x.q.AnsweredAt ?? 0).ThenByDescending(x => x.i).Take(MaxQuestions - pick.Count));
        return pick.OrderBy(x => x.i).Select(x => x.q).ToList();
    }

    /// <summary>An answer for the prompt: its latest part (the end holds the newest answer).</summary>
    public static string PromptAnswer(string answer) => answer.Length > MaxAnswerChars ? "..." + answer[^MaxAnswerChars..] : answer;

    public static string User(CoachSnapshot state, string recent, string fresh)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# STATE (already written; do not repeat it)");
        sb.AppendLine("Questions table (# | question | answer so far):");
        if (state.Questions.Count == 0) sb.AppendLine("(empty)");
        foreach (var q in PromptQuestions(state.Questions)) sb.AppendLine($"{q.Id} | {q.Question} | {PromptAnswer(q.Answer)}");
        if (state.NextSteps.Count > 0)
        {
            sb.AppendLine("Next steps:");
            foreach (var n in state.NextSteps.TakeLast(20)) sb.AppendLine($"- {n.Action} | {n.Who} | {n.When}");
        }
        if (state.Notes.Count > 0)
        {
            sb.AppendLine("Latest notes:");
            foreach (var n in state.Notes.TakeLast(15)) sb.AppendLine($"- {n.Time} {n.Text}");
        }
        if (state.Cues.Count > 0)
        {
            sb.AppendLine("Earlier cues:");
            foreach (var c in state.Cues.TakeLast(5)) sb.AppendLine($"- {c.Time} {c.Text}");
        }
        if (recent.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# TRANSCRIPT, LAST 3 MINUTES (context only, already handled)");
            sb.AppendLine(recent.Length > MaxRecentChars ? recent[^MaxRecentChars..] : recent);
        }
        sb.AppendLine();
        sb.AppendLine(NewHeader);
        sb.AppendLine(fresh.Length > MaxNewChars ? fresh[^MaxNewChars..] : fresh);
        return sb.ToString();
    }
}

public sealed class CoachOptions
{
    public required ICoachProvider Provider { get; init; }
    public required CoachSetup Setup { get; init; }
    /// <summary>The meeting clock (recording elapsed); coalescing, the cue gap and backoff run on it.</summary>
    public required Func<TimeSpan> Clock { get; init; }
    public ICoachToast? Toast { get; init; }
    public double CoalesceSeconds { get; init; } = 2.5;
    public double CueGapSeconds { get; init; } = 180;
    public double RecentSeconds { get; init; } = 180;
    public int MaxWakes { get; init; } = 300;
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>A timer checks for a due wake every 250 ms. Off in tests and the replay, which call Pump themselves.</summary>
    public bool AutoPump { get; init; } = true;
    public IEnumerable<string>? Languages { get; init; }
    public int Heartbeat { get; init; } = 6;
}

/// <summary>
/// The coach for one recording. <see cref="Post"/> never blocks (a lock and a list add), so live text and the recording
/// never wait on it. One model call at a time; lines arriving meanwhile go in the next wake. A failed call is retried
/// (twice at most) after a backoff of 30 s doubling to 10 min, with a short note in the panel.
/// </summary>
public sealed class CoachEngine : IDisposable
{
    private readonly CoachOptions _o;
    private ICoachProvider _provider;
    private readonly object _lock = new();
    private readonly CoachGate _gate;
    private readonly CueThrottle _cues;
    private readonly List<(TimeSpan At, string Line)> _recent = new();
    private readonly List<CoachLine> _pending = new();
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private readonly CoachSnapshot _state;
    private readonly Timer? _timer;
    private CoachSetup _setup;
    private string _system;
    private TimeSpan _due = TimeSpan.MaxValue;
    private TimeSpan _backoffUntil = TimeSpan.Zero;
    private int _retry;
    private int _failStreak;
    private bool _busy;
    private bool _flush;
    private bool _stopped;
    private bool _capNoted;
    private Task _inflight = Task.CompletedTask;
    /// <summary>Cancelled at Stop (after the grace) and at Dispose: ends a call in flight (the AI tool's tree is killed).</summary>
    private readonly CancellationTokenSource _life = new();
    /// <summary>The call in flight; cancelled by pause, Stop and Dispose.</summary>
    private CancellationTokenSource? _call;
    /// <summary>The call in flight was ended on purpose (pause, Stop, exit): not a failure, no backoff, nothing applied.</summary>
    private bool _abort;
    /// <summary>True while the status says the coach is fine (listening, or its last answer), so Stop may say it stopped.</summary>
    private bool _calm = true;

    /// <summary>"Claude Code, Sonnet 5.5", "Codex, the tool's own model", "Practice coach (no AI)".</summary>
    public static string Describe(ICoachProvider p) => p.Model == "rules" ? p.Name : $"{p.Name}, {CoachModels.Describe(p.Model)}";

    public CoachEngine(CoachOptions o)
    {
        _o = o;
        _provider = o.Provider;
        _setup = o.Setup;
        _system = CoachPrompt.System(_setup);
        _gate = new CoachGate(_setup.Terms, o.Languages, o.Heartbeat);
        _cues = new CueThrottle(TimeSpan.FromSeconds(o.CueGapSeconds));
        _state = new CoachSnapshot
        {
            Provider = o.Provider.Name,
            Model = o.Provider.Model,
            Local = o.Provider.IsLocal,
            Goal = _setup.Goal,
            Role = _setup.Role,
            Style = _setup.StyleName,
            Status = Loc.F("coach_listening", Describe(o.Provider)),
        };
        SeedQuestions(_setup);
        if (o.AutoPump) _timer = new Timer(_ => SafePump(), null, 250, 250);
    }

    /// <summary>Raised (on a worker thread) when the coach state or its status changed.</summary>
    public event Action? Changed;

    public int Wakes { get; private set; }

    /// <summary>
    /// The coach switch turned off during the recording: lines are ignored and nothing is sent until it is on again. A
    /// call in flight is ended at once (its AI tool killed, its request cancelled); its lines are dropped like the waiting ones.
    /// </summary>
    public bool Paused
    {
        get { lock (_lock) return _paused; }
        set
        {
            lock (_lock)
            {
                _paused = value;
                if (value)
                {
                    _pending.Clear();
                    if (_busy)
                    {
                        _abort = true;
                        try { _call?.Cancel(); } catch (ObjectDisposedException) { }
                    }
                }
                _state.Status = value ? Loc.T("coach_off_kept") : Loc.T("coach_listening_again");
                _calm = !value;
            }
            Raise();
        }
    }

    /// <summary>The languages the junk filter accepts, changed when live text locks a language (Auto).</summary>
    public void SetLanguages(IEnumerable<string> langs)
    {
        lock (_lock) _gate.Languages = langs.ToList();
    }

    public IReadOnlyList<string> Languages
    {
        get { lock (_lock) return _gate.Languages; }
    }

    private bool _paused;

    private void SeedQuestions(CoachSetup s)
    {
        var have = _state.Questions.Where(q => q.FromBrief).Select(q => q.Question).ToHashSet();
        foreach (var q in s.Questions.Where(q => !have.Contains(q)))
            _state.Questions.Add(new CoachQuestion { Id = $"Q{_state.Questions.Count + 1}", Question = q, FromBrief = true });
    }

    /// <summary>The AI the coach calls now (it changes when the user picks another during the recording).</summary>
    public ICoachProvider Provider
    {
        get { lock (_lock) return _provider; }
    }

    /// <summary>
    /// Another AI picked during the recording: the next wake goes to it (a call in flight finishes on the old one). The
    /// backoff of the old AI is cleared; the notes are kept. Local stays true only while every AI used ran on this PC.
    /// </summary>
    public void SetProvider(ICoachProvider p)
    {
        lock (_lock)
        {
            _provider = p;
            _state.Provider = p.Name;
            _state.Model = p.Model;
            _state.Local = _state.Local && p.IsLocal;
            _failStreak = 0;
            _backoffUntil = TimeSpan.Zero;
            if (_pending.Count > 0) _due = TimeSpan.Zero;
            _state.Status = Loc.F("coach_listening", Describe(p));
            _calm = true;
        }
        Raise();
    }

    /// <summary>A changed goal or brief during the meeting (the kit's re-aim): used from the next wake.</summary>
    public void UpdateSetup(CoachSetup s)
    {
        lock (_lock)
        {
            _setup = s;
            _system = CoachPrompt.System(s);
            _state.Goal = s.Goal;
            _state.Role = s.Role;
            _state.Style = s.StyleName;
            SeedQuestions(s);
        }
        Raise();
    }

    public void Post(CoachLine line)
    {
        GateResult r;
        lock (_lock)
        {
            if (_stopped || _paused) return;
            r = _gate.Check(line);
            if (r.Sys)
            {
                _state.Alerts.Add(new CoachEntry { At = line.At.TotalSeconds, Time = line.Stamp, Text = line.Text });
            }
            else if (!r.Dropped)
            {
                _recent.Add((line.At, Format(line)));
                var cut = line.At - TimeSpan.FromSeconds(_o.RecentSeconds);
                _recent.RemoveAll(x => x.At < cut);
            }
            if (r.Wake)
            {
                if (_pending.Count == 0) _due = _o.Clock() + TimeSpan.FromSeconds(_o.CoalesceSeconds);
                _pending.AddRange(r.Bundle);
                _pending.Add(r.Trigger!);
            }
        }
        if (r.Sys) Raise();
    }

    private string Format(CoachLine l) => $"[{l.Stamp}] [{_setup.Label(l)}] {l.Text}";

    private void SafePump()
    {
        try { Pump(); }
        catch (Exception ex) { Log.Error("coach pump", ex); }
    }

    /// <summary>Starts the model call for a due wake, if none runs. The timer calls it; tests and the replay call it too.</summary>
    public void Pump()
    {
        List<CoachLine> wake;
        string system, user;
        int retry;
        lock (_lock)
        {
            if (_busy || _pending.Count == 0) return;
            if (_stopped && !_flush) return;
            var now = _o.Clock();
            if (!_flush && (now < _due || now < _backoffUntil)) return;
            if (Wakes >= _o.MaxWakes)
            {
                if (!_capNoted)
                {
                    _capNoted = true;
                    _calm = false;
                    _state.Status = Loc.F("coach_cap", _o.MaxWakes);
                    _state.Alerts.Add(new CoachEntry { At = now.TotalSeconds, Time = Stamps.Of(now), Text = _state.Status });
                }
                _pending.Clear();
                Raise();
                return;
            }
            wake = _pending.ToList();
            _pending.Clear();
            _due = TimeSpan.MaxValue;
            retry = _retry;
            _retry = 0;
            _busy = true;
            Wakes++;
            var known = new HashSet<string>(wake.Select(Format));
            var recent = string.Join("\n", _recent.Select(x => x.Line).Where(x => !known.Contains(x)));
            system = _system;
            user = CoachPrompt.User(_state, recent, string.Join("\n", wake.Select(Format)));
            var provider = _provider;
            // made under the lock, so a pause or Stop right after this always reaches the call
            CancellationTokenSource cts;
            try { cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); }
            catch (ObjectDisposedException) { _busy = false; return; }
            cts.CancelAfter(_o.CallTimeout);
            _call = cts;
            _abort = false;
            _inflight = Task.Run(() => RunAsync(provider, wake, retry, system, user, cts));
        }
    }

    private async Task RunAsync(ICoachProvider provider, List<CoachLine> wake, int retry, string system, string user, CancellationTokenSource cts)
    {
        CoachReply reply;
        try
        {
            reply = await provider.CompleteAsync(system, user, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bool aborted;
            lock (_lock)
            {
                aborted = _abort || _life.IsCancellationRequested;
                if (aborted)
                {
                    // ended on purpose: no failure, no backoff, its lines dropped (pause cleared the waiting ones too)
                    _busy = false;
                    _call = null;
                    _abort = false;
                }
            }
            cts.Dispose();
            if (aborted)
            {
                Log.Info($"coach call ended on purpose ({provider.Name})");
                return;
            }
            var why = ex is OperationCanceledException ? Loc.F("coach_no_answer", _o.CallTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)) : CoachProviders.Safe(ex.Message, null);
            lock (_lock)
            {
                _busy = false;
                _call = null;
                _failStreak++;
                _state.Failures++;
                var wait = Math.Min(600, 30 * Math.Pow(2, _failStreak - 1));
                _backoffUntil = _o.Clock() + TimeSpan.FromSeconds(wait);
                if (retry < 2)
                {
                    // nothing of this wake was applied: its lines go first in the next one
                    _pending.InsertRange(0, wake);
                    _retry = retry + 1;
                    _due = TimeSpan.Zero;
                }
                _state.Status = Loc.F("coach_retry", why, wait.ToString("0", CultureInfo.InvariantCulture));
                _calm = false;
            }
            Log.Warn($"coach call failed ({provider.Name}): {why}");
            Raise();
            return;
        }

        var at = wake.Count > 0 ? wake[^1].At : _o.Clock();
        List<string> cues;
        bool late;
        lock (_lock)
        {
            _busy = false;
            _call = null;
            // an answer that came back after a pause or the end of Stop is not applied (the screen already said so)
            late = _abort || _life.IsCancellationRequested;
            _abort = false;
            cues = new List<string>();
            if (!late)
            {
                _failStreak = 0;
                _state.Calls++;
                _state.InputTokens += reply.InputTokens;
                _state.OutputTokens += reply.OutputTokens;
                _state.CacheReadTokens += reply.CacheReadTokens;
                _state.CacheWriteTokens += reply.CacheWriteTokens;
                if (reply.Estimated) _state.TokensEstimated = true;
                cues = Apply(reply.Text, at);
                _state.Status = Loc.F("coach_last_answer", Stamps.Of(at));
                _calm = true;
            }
        }
        cts.Dispose();
        if (late) return;
        if (_o.Toast is { } toast)
            foreach (var c in cues)
            {
                try { toast.Show(Loc.T("toast_title"), c); }
                catch (Exception ex) { Log.Warn($"coach toast failed: {ex.Message}"); }
            }
        Raise();
        SafePump();
    }

    /// <summary>The kit's normReply: a tag wrapped in markdown ("- LOG: x", "**QA:** x", "1. NEXT: x") is still that tag.</summary>
    public static string Normalize(string l)
    {
        var t = Regex.Replace(l.Trim(), @"^(?:[>\-*•]\s+|\d+[.)]\s+)+", "");
        var w = Regex.Match(t, @"^(\*\*|__|`+)");
        var u = w.Success ? t[w.Length..] : t;
        var m = Regex.Match(u, @"^(LOG|CUE|QA|NEXT|ESCALATE)\s*[:：]\s*(?:\*\*|__)?\s*(.*)$", RegexOptions.Singleline);
        if (!m.Success) return l.Trim();
        var rest = m.Groups[2].Value.Trim();
        if (w.Success && rest.EndsWith(w.Value, StringComparison.Ordinal)) rest = rest[..^w.Length].Trim();
        return rest.Length > 0 ? $"{m.Groups[1].Value}: {rest}" : m.Groups[1].Value;
    }

    /// <summary>The kit's number guard: a LOG, QA or NEXT line with a digit (beyond its row id) is marked unconfirmed.</summary>
    public static string MarkNumbers(string kind, string body)
    {
        if (kind is not ("LOG" or "QA" or "NEXT")) return body;
        var check = body;
        if (kind == "QA")
        {
            // only the answer: "Q1 | answer" or "+ | question | answer"
            var parts = body.Split('|');
            check = parts.Length > 1 ? string.Join("|", parts.Skip(parts[0].Trim() is "+" or "" ? 2 : 1)) : "";
            if (check.Trim().Length == 0) return body;
        }
        if (Regex.IsMatch(check, @"(to confirm|c[aầ]n x[aá]c nh[aậ]n)[^\]]*\]?[\s).,;]*$", RegexOptions.IgnoreCase)) return body;
        return Regex.IsMatch(check, @"\d") ? body + " [to confirm]" : body;
    }

    /// <summary>Applies the reply's lines to the state; returns the cues that passed the 3-minute throttle (for the toast).</summary>
    private List<string> Apply(string reply, TimeSpan at)
    {
        var cues = new List<string>();
        var time = Stamps.Of(at);
        foreach (var raw in reply.Split('\n'))
        {
            var l = Normalize(raw);
            var colon = l.IndexOf(':');
            if (colon <= 0) continue;
            var kind = l[..colon];
            if (kind is not ("LOG" or "CUE" or "QA" or "NEXT" or "ESCALATE")) continue;
            var body = l[(colon + 1)..].Trim();
            if (body.Length == 0) continue;
            body = MarkNumbers(kind, body);
            // a wake retried after a lost answer must not write the same line twice
            if (!_written.Add(kind + ":" + body)) continue;
            switch (kind)
            {
                case "LOG":
                    _state.Notes.Add(new CoachEntry { At = at.TotalSeconds, Time = time, Text = Regex.Replace(body, @"^\d\d:\d\d\s+", "") });
                    break;
                case "CUE":
                    if (!_cues.TryTake(at))
                    {
                        _state.ThrottledCues++;
                        break;
                    }
                    _state.Cues.Add(new CoachEntry { At = at.TotalSeconds, Time = time, Text = body });
                    cues.Add(body);
                    break;
                case "ESCALATE":
                    _state.Alerts.Add(new CoachEntry { At = at.TotalSeconds, Time = time, Text = body });
                    break;
                case "NEXT":
                    var n = body.Split('|').Select(x => x.Trim()).ToArray();
                    _state.NextSteps.Add(new CoachNextStep
                    {
                        At = at.TotalSeconds, Time = time, Action = n[0], Who = n.Length > 1 ? n[1] : "",
                        When = n.Length > 2 ? string.Join(" - ", n.Skip(2).Where(x => x.Length > 0)) : "",
                    });
                    break;
                case "QA":
                    ApplyQa(body, at);
                    break;
            }
        }
        return cues;
    }

    private static string IdKey(string s) => Regex.Replace(CoachGate.Fold(s).TrimStart('#').Trim(), @"^([a-z]*)[\s\-_.]*(\d+)$", "$1$2");

    private void ApplyQa(string body, TimeSpan at)
    {
        var p = body.Split('|').Select(x => x.Trim()).ToArray();
        var id = p[0];
        if (id is "+" or "")
        {
            if (p.Length < 2 || p[1].Length == 0) return;
            var first = p.Length > 2 ? string.Join(" - ", p.Skip(2).Where(x => x.Length > 0)) : "";
            // the same question asked again (another wording of case and marks) is one row: its answer joins that row
            var fold = CoachGate.Fold(p[1]);
            var same = _state.Questions.FirstOrDefault(q => CoachGate.Fold(q.Question) == fold);
            if (same is not null)
            {
                if (first.Length > 0)
                {
                    same.Answer = CapAnswer(same.Answer.Length > 0 ? $"{same.Answer} / {Stamps.Of(at)} {first}" : first);
                    same.AnsweredAt = at.TotalSeconds;
                }
                return;
            }
            _state.Questions.Add(new CoachQuestion
            {
                Id = $"Q{_state.Questions.Count + 1}", Question = p[1],
                Answer = CapAnswer(first),
                AnsweredAt = first.Length > 0 ? at.TotalSeconds : null,
            });
            return;
        }
        var answer = string.Join(" - ", p.Skip(1).Where(x => x.Length > 0));
        if (answer.Length == 0) return;
        var key = IdKey(id);
        var digits = Regex.Replace(key, "^[a-z]+", "");
        var row = _state.Questions.FirstOrDefault(q => IdKey(q.Id) == key)
                  ?? _state.Questions.FirstOrDefault(q => digits.Length > 0 && Regex.Replace(IdKey(q.Id), "^[a-z]+", "") == digits && (key == digits || IdKey(q.Id) == digits));
        if (row is null)
        {
            _state.Notes.Add(new CoachEntry { At = at.TotalSeconds, Time = Stamps.Of(at), Text = $"[Q/A {id}] {answer}" });
            return;
        }
        row.Answer = CapAnswer(row.Answer.Length > 0 ? $"{row.Answer} / {Stamps.Of(at)} {answer}" : $"{Stamps.Of(at)} {answer}");
        row.AnsweredAt = at.TotalSeconds;
    }

    public const int MaxAnswerChars = 300;

    /// <summary>A row's answer grows each time the meeting adds to it: kept to its newest 300 characters.</summary>
    public static string CapAnswer(string answer) => answer.Length > MaxAnswerChars ? "... " + answer[^(MaxAnswerChars - 4)..].TrimStart() : answer;

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("coach changed handler", ex); }
    }

    public CoachSnapshot Snapshot()
    {
        lock (_lock) return Copy(_state);
    }

    private static CoachSnapshot Copy(CoachSnapshot s) => JsonSerializer.Deserialize<CoachSnapshot>(JsonSerializer.Serialize(s))!;

    /// <summary>Waits until no call runs (tests and the replay).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task t;
            lock (_lock)
            {
                if (!_busy) return;
                t = _inflight;
            }
            await t.ConfigureAwait(false);
        }
    }

    /// <summary>Stop: the lines waiting for a wake are sent now, the running call may finish within the grace; then nothing more.</summary>
    public async Task<CoachSnapshot> StopAsync(TimeSpan grace)
    {
        _timer?.Dispose();
        lock (_lock)
        {
            _stopped = true;
            _flush = true;
            _backoffUntil = TimeSpan.Zero;
        }
        var until = DateTime.UtcNow + grace;
        try
        {
            while (DateTime.UtcNow < until)
            {
                Pump();
                Task t;
                lock (_lock)
                {
                    if (!_busy && _pending.Count == 0) break;
                    t = _inflight;
                }
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                await Task.WhenAny(t, Task.Delay(left)).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_lock)
            {
                _flush = false;
                _pending.Clear();
            }
        }
        // the grace is over: a call still running is ended now (its AI tool and everything it started are killed)
        Task inflight;
        bool wasBusy;
        lock (_lock)
        {
            wasBusy = _busy;
            if (_busy) _abort = true;
            inflight = _inflight;
        }
        _life.Cancel();
        if (wasBusy) await Task.WhenAny(inflight, Task.Delay(EndWait)).ConfigureAwait(false);
        lock (_lock)
        {
            if (wasBusy) _state.Status = Loc.T("coach_stopped_running");
            else if (_calm) _state.Status = Loc.T("coach_stopped");
            return Copy(_state);
        }
    }

    /// <summary>How long Stop and Dispose wait for a killed call to unwind.</summary>
    public static readonly TimeSpan EndWait = TimeSpan.FromSeconds(3);

    /// <summary>The app closing, or the coach of a recording thrown away: a call in flight is ended and its tool killed.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        Task inflight;
        lock (_lock)
        {
            _stopped = true;
            if (_busy) _abort = true;
            inflight = _inflight;
        }
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        try { inflight.Wait(EndWait); }
        catch (Exception) { } // a failed call already logged itself
    }
}

/// <summary>coach.md and coach.json beside the recording.</summary>
public static class CoachStore
{
    public const string MarkdownFile = "coach.md";
    public const string JsonFile = "coach.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>coach.md and coach.json beside a stopped recording, plus the coach entry and file list in its session.json.</summary>
    public static IReadOnlyList<string> SaveForSession(string dir, Session.SessionManifest manifest, CoachSnapshot snap, bool sentOut)
    {
        var files = Save(dir, manifest.Id, snap);
        lock (manifest)
        {
            manifest.Coach = new Session.CoachInfo
            {
                Provider = snap.Provider,
                Model = snap.Model,
                SentOut = sentOut,
                Calls = snap.Calls,
                Failures = snap.Failures,
                InputTokens = snap.InputTokens + snap.CacheWriteTokens,
                OutputTokens = snap.OutputTokens,
                CachedTokens = snap.CacheReadTokens,
                TokensEstimated = snap.TokensEstimated,
                Cues = snap.Cues.Count,
                Questions = snap.Questions.Count,
                QuestionsAnswered = snap.Questions.Count(q => q.Answer.Length > 0),
                NextSteps = snap.NextSteps.Count,
                Goal = snap.Goal.Length > 0 ? snap.Goal : null,
                Files = files.ToList(),
            };
            manifest.Files.RemoveAll(f => f.Name is MarkdownFile or JsonFile);
            manifest.Files.Add(new Session.FileEntry { Name = MarkdownFile, Description = "live coach notes: cues, questions with answers, next steps, notes (times from the recording start)" });
            manifest.Files.Add(new Session.FileEntry { Name = JsonFile, Description = "the same coach notes with seconds, plus the AI used, calls and tokens" });
        }
        Transcription.TranscriptionService.SaveManifest(dir, manifest);
        return files;
    }

    public static IReadOnlyList<string> Save(string dir, string sessionId, CoachSnapshot s)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Coach notes - recording {sessionId}");
        md.AppendLine();
        var model = string.IsNullOrWhiteSpace(s.Model) ? "the tool's own default model" : $"model {CoachModels.Find(s.Model)?.Name ?? s.Model}";
        md.AppendLine($"Coach: {s.Provider}, {model}. {s.UsageLine}.");
        if (s.Goal.Length > 0) md.AppendLine($"Goal: {s.Goal}");
        if (s.Role.Length > 0) md.AppendLine($"Your role: {s.Role}");
        md.AppendLine($"Style: {s.Style}. Times are from the start of the recording.");
        Section(md, "Cues (what to say or ask)", s.Cues.Select(c => $"- {c.Time} {c.Text}"));
        if (s.Questions.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Questions");
            md.AppendLine();
            md.AppendLine("| # | Question | Answer |");
            md.AppendLine("|---|---|---|");
            foreach (var q in s.Questions) md.AppendLine($"| {Cell(q.Id)} | {Cell(q.Question)} | {Cell(q.Answer)} |");
        }
        if (s.NextSteps.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Next steps");
            md.AppendLine();
            md.AppendLine("| Time | What | Who | When |");
            md.AppendLine("|---|---|---|---|");
            foreach (var n in s.NextSteps) md.AppendLine($"| {n.Time} | {Cell(n.Action)} | {Cell(n.Who)} | {Cell(n.When)} |");
        }
        Section(md, "Notes", s.Notes.Select(n => $"- {n.Time} {n.Text}"));
        Section(md, "Needs attention", s.Alerts.Select(a => $"- {a.Time} {a.Text}"));
        File.WriteAllText(Path.Combine(dir, MarkdownFile), md.ToString());
        var doc = new { sessionId, s.Provider, s.Model, s.Goal, s.Role, s.Style, usage = new { s.Calls, s.Failures, s.ThrottledCues, s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheWriteTokens, s.TokensEstimated }, s.Cues, s.Questions, s.NextSteps, s.Notes, s.Alerts };
        File.WriteAllText(Path.Combine(dir, JsonFile), JsonSerializer.Serialize(doc, Options));
        return new[] { MarkdownFile, JsonFile };
    }

    private static void Section(StringBuilder md, string title, IEnumerable<string> lines)
    {
        var l = lines.ToList();
        if (l.Count == 0) return;
        md.AppendLine();
        md.AppendLine($"## {title}");
        md.AppendLine();
        foreach (var x in l) md.AppendLine(x);
    }

    private static string Cell(string s) => s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();
}
