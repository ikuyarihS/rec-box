using System.ComponentModel;
using System.Globalization;

namespace RecBox.Core;

/// <summary>
/// The interface language (English or Tiếng Việt) and its texts. One instance for the whole app: the window binds its
/// texts to <see cref="this[string]"/>, so <see cref="Set"/> changes every text on screen at once (no restart). Texts made
/// in code read <see cref="T"/> / <see cref="F"/> when they are made, and are made again on <see cref="Changed"/>.
/// Logs, file names and the files RecBox writes stay English. The coach's output language is its own setting.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly IReadOnlyList<string> Languages = new[] { "en", "vi" };

    /// <summary>The Windows display language when RecBox started (the default when nothing is saved).</summary>
    public static readonly CultureInfo OsCulture = CultureInfo.CurrentUICulture;

    public static Loc Instance { get; } = new();

    private volatile string _language = Default(OsCulture);

    /// <summary>The app uses <see cref="Instance"/>; tests make their own, so a switch never changes what other tests read.</summary>
    internal Loc()
    {
    }

    /// <summary>"en" or "vi".</summary>
    public string Language => _language;

    /// <summary>The text of a key in the current language (the key itself when it is unknown, so a gap shows).</summary>
    public string this[string key] => Get(_language, key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after a switch, once every binding was told (code-made texts are made again here).</summary>
    public event Action? Changed;

    /// <summary>Switches the interface language; anything but "vi" is English. No event when nothing changes.</summary>
    public void Set(string? language)
    {
        var to = Normalize(language);
        // worker processes started from now on write their messages in this language too
        if (ReferenceEquals(this, Instance)) Environment.SetEnvironmentVariable(EnvVar, to);
        if (to == _language) return;
        _language = to;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        Changed?.Invoke();
    }

    public static string T(string key) => Instance[key];

    /// <summary>The interface language handed to worker processes (they have no settings of their own).</summary>
    public const string EnvVar = "RECBOX_UI_LANG";

    /// <summary>In a worker process: the language of the RecBox that started it.</summary>
    public static void FollowParent()
    {
        if (Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } l) Instance.Set(l);
    }

    /// <summary>A text with values in its {0}, {1}... (numbers written the same in both languages).</summary>
    public static string F(string key, params object?[] args)
    {
        var text = T(key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    public static bool Has(string key) => Strings.Table.ContainsKey(key);

    /// <summary>Vietnamese for a vi-* Windows display language, English for any other.</summary>
    public static string Default(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            if (string.Equals(c.TwoLetterISOLanguageName, "vi", StringComparison.OrdinalIgnoreCase)) return "vi";
        return "en";
    }

    /// <summary>The saved choice ("en" or "vi"), or the Windows language when nothing (or something unknown) is saved.</summary>
    public static string Resolve(string? saved) => saved is "en" or "vi" ? saved : Default(OsCulture);

    public static string Get(string language, string key) =>
        Strings.Table.TryGetValue(key, out var t) ? (language == "vi" ? t.Vi : t.En) : key;

    private static string Normalize(string? language) => language == "vi" ? "vi" : "en";
}

/// <summary>
/// Every text of the interface, in English and Vietnamese. Plain hyphens only. A text with {0}, {1}... has the same
/// numbered places in both languages (tested).
/// </summary>
public static class Strings
{
    private static readonly (string Key, string En, string Vi)[] Rows =
    {
        // ---------- recording ----------
        ("src_mic", "Microphone", "Micrô"),
        ("src_speaker", "System audio", "Âm thanh máy tính"),
        ("save_mic_track", "Saving microphone track", "Đang lưu rãnh micrô"),
        ("save_speaker_track", "Saving system audio track", "Đang lưu rãnh âm thanh máy tính"),
        ("save_mixed_track", "Saving mixed track", "Đang lưu rãnh trộn"),
        ("save_cleaning", "Cleaning up the speech copy", "Đang làm sạch bản giọng nói"),
        ("rec_switched", "Switched to {0}", "Đã chuyển sang {0}"),
        ("rec_device_unavailable", "device unavailable", "thiết bị không dùng được"),
        ("rec_switch_failed", "Could not switch device ({0}), retrying", "Không chuyển được thiết bị ({0}), đang thử lại"),
        ("rec_no_mic", "No microphone available", "Không có micrô nào"),
        ("rec_no_playback", "No playback device available", "Không có thiết bị phát âm thanh nào"),
        ("rec_open_failed", "Could not open {0}: {1}", "Không mở được {0}: {1}"),
        ("rec_start_failed", "Could not start {0}: {1}", "Không khởi động được {0}: {1}"),
        ("rec_device_changed", "Device changed, now recording {0}", "Thiết bị đã đổi, giờ đang ghi {0}"),
        ("rec_device_lost", "Device lost and no replacement found", "Mất thiết bị và không tìm thấy thiết bị thay thế"),

        // ---------- AI tools, models, usage ----------
        ("model_cli_default", "the tool's own default model", "mô hình mặc định của công cụ"),
        ("model_tier_fast", "fastest", "nhanh nhất"),
        ("model_tier_balanced", "balanced, the default", "cân bằng, mặc định"),
        ("model_tier_strong", "strongest, slowest", "mạnh nhất, chậm nhất"),
        ("usage_per_hour", "About {0} tokens an hour with these choices (estimate).", "Khoảng {0} token mỗi giờ với các lựa chọn này (ước tính)."),
        ("usage_local", "{0} call(s) to the model on this PC", "{0} lượt gọi mô hình trên máy này"),
        ("usage_tokens", "{0} call(s), {1} tokens in, {2} out", "{0} lượt gọi, {1} token vào, {2} token ra"),
        ("usage_cached", ", {0} cached", ", {0} từ bộ nhớ đệm"),
        ("usage_estimated", "(estimate)", "(ước tính)"),
        ("ai_claude", "Claude Code", "Claude Code"),
        ("ai_codex", "Codex", "Codex"),
        ("ai_gemini", "Gemini CLI", "Gemini CLI"),
        ("ai_fake", "Practice coach (no AI)", "Trợ lý tập dượt (không dùng AI)"),
        ("ai_option_ClaudeCli", "Claude Code on this PC", "Claude Code trên máy này"),
        ("ai_option_CodexCli", "Codex CLI on this PC", "Codex CLI trên máy này"),
        ("ai_option_GeminiCli", "Gemini CLI on this PC", "Gemini CLI trên máy này"),
        ("ai_option_OpenAiCompatible", "A model server on this PC (Ollama, LM Studio)", "Máy chủ mô hình trên máy này (Ollama, LM Studio)"),
        ("ai_not_installed", "(not installed)", "(chưa cài)"),
        ("ai_local_server", "a model server on this PC ({0})", "máy chủ mô hình trên máy này ({0})"),
        ("ai_missing_suffix", "{0} (not installed)", "{0} (chưa cài)"),
        ("cli_missing", "{0} is not installed on this PC. Install it and sign in once, or pick another AI tool.", "{0} chưa được cài trên máy này. Hãy cài và đăng nhập một lần, hoặc chọn công cụ AI khác."),
        ("cli_not_started", "{0} did not start.", "{0} không khởi động được."),
        ("endpoint_missing", "Set the endpoint, the address of the model server on this PC, for example http://localhost:11434/v1 for Ollama.", "Hãy đặt endpoint, tức địa chỉ máy chủ mô hình trên máy này, ví dụ http://localhost:11434/v1 cho Ollama."),
        ("endpoint_not_local", "Only a model server on this PC (localhost) can be used: RecBox never sends text to a cloud API.", "Chỉ dùng được máy chủ mô hình trên máy này (localhost): RecBox không bao giờ gửi văn bản tới API đám mây."),
        ("tool_status_local", "A model server on this PC: no sign-in, nothing leaves your computer.", "Máy chủ mô hình trên máy này: không cần đăng nhập, không có gì rời khỏi máy tính của bạn."),
        ("tool_status_ready", "{0} is installed. It uses your own {0} sign-in; RecBox keeps no keys.", "Đã cài {0}. Công cụ dùng tài khoản {0} của bạn; RecBox không giữ khóa nào."),
        ("tool_status_missing", "{0} is not installed on this PC. Install it and sign in once, or pick another AI tool.", "{0} chưa được cài trên máy này. Hãy cài và đăng nhập một lần, hoặc chọn công cụ AI khác."),
        ("model_hint_claude", "Empty: Claude Code's own default model. Pick one or type any model name.", "Để trống: dùng mô hình mặc định của Claude Code. Chọn một mô hình hoặc gõ tên bất kỳ."),
        ("model_hint_tool", "Empty: the tool's own default model. Or type a model name it knows.", "Để trống: dùng mô hình mặc định của công cụ. Hoặc gõ tên mô hình mà công cụ biết."),

        // ---------- privacy ----------
        ("privacy_offline", "On this PC, offline. Nothing leaves your computer.", "Trên máy này, ngoại tuyến. Không có gì rời khỏi máy tính của bạn."),
        ("privacy_local_model", "On this PC, offline. Nothing leaves your computer: the AI runs on a model server on this PC.", "Trên máy này, ngoại tuyến. Không có gì rời khỏi máy tính của bạn: AI chạy trên máy chủ mô hình ngay trên máy này."),
        ("privacy_sent", "Audio stays on this PC; {1} are sent through {0}.", "Âm thanh ở lại trên máy này; {1} được gửi qua {0}."),
        ("privacy_for_both", "the live text, your meeting brief and the lines to translate", "phụ đề trực tiếp, ghi chú chuẩn bị và các câu cần dịch"),
        ("privacy_for_coach", "while the coach is on, the live text and your meeting brief", "khi trợ lý bật, phụ đề trực tiếp và ghi chú chuẩn bị cuộc họp"),
        ("privacy_for_translate", "the lines to translate (live text and the transcript)", "các câu cần dịch (phụ đề trực tiếp và bản chép lời)"),
        ("recv_claude", "Claude Code (Anthropic)", "Claude Code (Anthropic)"),
        ("recv_codex", "Codex (OpenAI)", "Codex (OpenAI)"),
        ("recv_gemini", "Gemini CLI (Google)", "Gemini CLI (Google)"),
        ("recv_endpoint", "the address you set", "địa chỉ bạn đặt"),
        ("recv_nobody", "nobody", "không ai"),

        // ---------- coach ----------
        ("style_balanced", "Balanced coach", "Trợ lý cân bằng"),
        ("style_sales", "Sales or partner meeting", "Họp bán hàng hoặc đối tác"),
        ("style_interview", "Interview", "Phỏng vấn"),
        ("style_technical", "Technical review", "Rà soát kỹ thuật"),
        ("style_notes", "Notes only", "Chỉ ghi chú"),
        ("coachlang_same", "Same as the meeting", "Giống ngôn ngữ cuộc họp"),
        ("coach_starts_itself", "It starts by itself once that is fixed.", "Trợ lý tự khởi động khi việc đó được khắc phục."),
        ("coach_started", "Coach started ({0}).", "Trợ lý đã bắt đầu ({0})."),
        ("rec_need_source", "Turn on the microphone or system audio first.", "Hãy bật micrô hoặc âm thanh máy tính trước."),
        ("cli_no_answer", "{0} answered nothing (exit {1}): {2}", "{0} không trả lời gì (mã thoát {1}): {2}"),
        ("slides_no_window", "Slides: no window with \"{0}\" in its title is open.", "Ảnh slide: không có cửa sổ nào đang mở có \"{0}\" trong tiêu đề."),
        ("ltn_end_left", "({0:0} s of speech at the end were not transcribed live; the full transcript has them)", "({0:0} giây lời nói ở cuối chưa được chép trực tiếp; bản chép lời đầy đủ có phần này)"),
        ("ltn_skipped", "(skipped {0:0} s of speech to catch up; the full transcript has it)", "(đã bỏ qua {0:0} giây lời nói để bắt kịp; bản chép lời đầy đủ có phần này)"),
        ("ltn_no_engine", "(could not transcribe {0}: the speech engine did not start)", "(không chép được {0}: bộ nhận dạng giọng nói không khởi động)"),
        ("ltn_restart", "(could not transcribe {0}: {1}; restarting the speech engine)", "(không chép được {0}: {1}; đang khởi động lại bộ nhận dạng giọng nói)"),
        ("ltn_failed", "(could not transcribe {0}: {1})", "(không chép được {0}: {1})"),
        ("ltn_span", "{0} to {1}", "{0} đến {1}"),
        ("lte_not_started", "The live speech engine could not start ({0}).", "Bộ nhận dạng giọng nói trực tiếp không khởi động được ({0})."),
        ("lte_load_timeout", "The live speech engine did not load the model in {0:0} s.", "Bộ nhận dạng giọng nói trực tiếp không nạp xong mô hình trong {0:0} giây."),
        ("lte_stopped_why", "The live speech engine stopped ({0}).", "Bộ nhận dạng giọng nói trực tiếp đã dừng ({0})."),
        ("lte_no_answer", "The live speech engine did not answer in {0:0} s.", "Bộ nhận dạng giọng nói trực tiếp không trả lời trong {0:0} giây."),
        ("lte_no_model", "The live speech engine could not load the model.", "Bộ nhận dạng giọng nói trực tiếp không nạp được mô hình."),
        ("lte_stopped_code", "The live speech engine stopped: {0} (worker exit code {1})", "Bộ nhận dạng giọng nói trực tiếp đã dừng: {0} (mã thoát của tiến trình {1})"),
        ("lte_stopped", "The live speech engine stopped", "Bộ nhận dạng giọng nói trực tiếp đã dừng"),
        ("lte_failed_start", "Live text failed to start: {0}", "Phụ đề trực tiếp không khởi động được: {0}"),
        ("gpu_lt_failed", "The graphics card (Vulkan) failed during live text: {0}", "Card đồ họa (Vulkan) bị lỗi khi chạy phụ đề trực tiếp: {0}"),
        ("gpu_lt_unavailable", "The graphics card (Vulkan) is not available for live text; used {0}.", "Card đồ họa (Vulkan) không dùng được cho phụ đề trực tiếp; đã dùng {0}."),
        ("gpu_lt_start_failed", "The graphics card (Vulkan) failed to start live text: {0}", "Card đồ họa (Vulkan) không khởi động được phụ đề trực tiếp: {0}"),
        ("gpu_tx_failed", "The graphics card (Vulkan) failed: {0}", "Card đồ họa (Vulkan) bị lỗi: {0}"),
        ("gpu_tx_unavailable", "The graphics card (Vulkan) is not available on this PC; used {0}.", "Card đồ họa (Vulkan) không dùng được trên máy này; đã dùng {0}."),
        ("gs_slower", "The graphics card was slower than the processor with {0} ({1:0.0}x against {2:0.0}x real time).", "Card đồ họa chậm hơn bộ xử lý với {0} ({1:0.0}x so với {2:0.0}x thời gian thực)."),
        ("txe_model_missing", "The {0} model is not downloaded yet. Open Settings and download it.", "Mô hình {0} chưa được tải về. Hãy mở Cài đặt và tải về."),
        ("txe_cancelled", "Transcription was cancelled.", "Đã hủy chép lời."),
        ("txe_failed", "Transcription failed: {0}", "Chép lời thất bại: {0}"),
        ("txe_failed_on", "Transcription failed on {0}: {1}", "Chép lời thất bại trên {0}: {1}"),
        ("txe_this_pc", "this PC", "máy này"),
        ("txe_not_started", "The speech engine could not start ({0}).", "Bộ nhận dạng giọng nói không khởi động được ({0})."),
        ("txe_no_result", "The speech engine ended without a result (worker exit code {0}).", "Bộ nhận dạng giọng nói đã kết thúc mà không có kết quả (mã thoát của tiến trình {0})."),
        ("txe_stopped", "The speech engine stopped: {0} (worker exit code {1}). Recordings are not affected.", "Bộ nhận dạng giọng nói đã dừng: {0} (mã thoát của tiến trình {1}). Các bản ghi âm không bị ảnh hưởng."),
        ("txe_with_code", "{0} (worker exit code {1})", "{0} (mã thoát của tiến trình {1})"),
        ("wx_finished", "finished", "đã xong"),
        ("wx_failed", "the speech engine reported an error", "bộ nhận dạng giọng nói báo lỗi"),
        ("wx_nomem", "not enough free memory", "không đủ bộ nhớ trống"),
        ("wx_badargs", "bad worker arguments", "tham số của tiến trình không hợp lệ"),
        ("wx_parent", "RecBox closed", "RecBox đã đóng"),
        ("wx_abort", "the speech engine aborted (often a graphics driver or memory problem)", "bộ nhận dạng giọng nói bị dừng đột ngột (thường do trình điều khiển đồ họa hoặc bộ nhớ)"),
        ("wx_avx", "the processor lacks an instruction the speech engine needs (AVX/AVX2)", "bộ xử lý thiếu lệnh mà bộ nhận dạng giọng nói cần (AVX/AVX2)"),
        ("wx_av", "native crash (access violation), often a graphics driver problem", "lỗi nghiêm trọng (vi phạm truy cập bộ nhớ), thường do trình điều khiển đồ họa"),
        ("wx_failfast", "native crash (fail-fast or stack buffer overrun)", "lỗi nghiêm trọng (fail-fast hoặc tràn bộ đệm ngăn xếp)"),
        ("wx_stack", "native crash (stack overflow)", "lỗi nghiêm trọng (tràn ngăn xếp)"),
        ("wx_oom", "out of memory", "hết bộ nhớ"),
        ("wx_cpp", "native crash (unhandled C++ exception)", "lỗi nghiêm trọng (ngoại lệ C++ không được xử lý)"),
        ("wx_dotnet", "unhandled .NET error in the worker", "lỗi .NET không được xử lý trong tiến trình"),
        ("wx_dll", "a native library (DLL) is missing", "thiếu một thư viện gốc (DLL)"),
        ("wx_dllinit", "the worker could not start (DLL initialization failed)", "tiến trình không khởi động được (khởi tạo DLL thất bại)"),
        ("wx_unknown", "the speech engine stopped unexpectedly", "bộ nhận dạng giọng nói dừng bất ngờ"),
        ("mem_refusal", "Not enough free memory to load the {0} model: it needs about {1}, {2} is free. Close other apps and record again, or pick {3} in Settings. The recording itself is saved.", "Không đủ bộ nhớ trống để nạp mô hình {0}: cần khoảng {1}, còn trống {2}. Hãy đóng các ứng dụng khác rồi ghi âm lại, hoặc chọn {3} trong Cài đặt. Bản ghi âm vẫn được lưu."),
        ("mem_pick_any", "a smaller model (or Auto)", "một mô hình nhỏ hơn (hoặc Tự động)"),
        ("mem_pick_named", "the smaller {0} model (or Auto)", "mô hình nhỏ hơn {0} (hoặc Tự động)"),
        ("or_word", "or", "hoặc"),
        ("mp_no_avx2", "the processor lacks AVX2, only the slower no-AVX build runs", "bộ xử lý không có AVX2, chỉ chạy được bản không AVX chậm hơn"),
        ("mp_small_floor", "Small is the smallest model good enough beyond English", "Small là mô hình nhỏ nhất đủ tốt cho ngôn ngữ ngoài tiếng Anh"),
        ("mp_ram", "{0} RAM with {1} free", "{0} RAM, còn trống {1}"),
        ("mp_gpu_room", "{0} has room for the most accurate model", "{0} đủ chỗ cho mô hình chính xác nhất"),
        ("mp_cores_strong", "{0} processor cores and {1} RAM: the most accurate model, slower than recording time but after the recording", "{0} nhân xử lý và {1} RAM: mô hình chính xác nhất, chậm hơn thời gian ghi âm nhưng chạy sau khi ghi âm"),
        ("mp_cores", "{0} processor cores and {1} RAM", "{0} nhân xử lý và {1} RAM"),
        ("mp_cores_medium", "{0} processor cores keep up with Medium", "{0} nhân xử lý theo kịp Medium"),
        ("mp_cores_light", "{0} processor cores and no graphics card with room for Medium: {1}, catching up when it falls behind", "{0} nhân xử lý và không có card đồ họa đủ chỗ cho Medium: {1}, bắt kịp khi bị chậm"),
        ("mp_stepped", "stepped down to {0} for free memory", "hạ xuống {0} vì bộ nhớ trống"),
        ("mp_auto_picked", "Auto picked {0} for {1}: {2}", "Tự động chọn {0} cho {1}: {2}"),
        ("mp_for_live", "live text", "phụ đề trực tiếp"),
        ("mp_for_transcript", "the transcript", "bản chép lời"),
        ("mp_chosen", "{0} (chosen in Settings)", "{0} (chọn trong Cài đặt)"),
        ("mp_not_downloaded", "{0} is not downloaded, using {1}", "{0} chưa được tải về, dùng {1}"),
        ("mp_cpu_baseline", "this time on the processor instead: measuring it once to compare with the graphics card (it has a speed for {0}, the processor none yet)", "lần này chạy trên bộ xử lý: đo một lần để so với card đồ họa (card đã có tốc độ cho {0}, bộ xử lý thì chưa)"),
        ("mp_on_gpu", "on the graphics card (Vulkan)", "trên card đồ họa (Vulkan)"),
        ("mp_on_cpu", "on the processor", "trên bộ xử lý"),
        ("mp_threads", "{0} thread(s) of {1} cores", "{0} luồng trên {1} nhân"),
        ("gf_off", "the graphics card is off in Settings", "card đồ họa đang tắt trong Cài đặt"),
        ("gf_no_avx2", "the processor lacks AVX2, so only the no-AVX processor build runs", "bộ xử lý không có AVX2, nên chỉ chạy được bản không AVX trên bộ xử lý"),
        ("gf_none", "no graphics card found", "không tìm thấy card đồ họa"),
        ("gf_integrated", "{0} is integrated graphics (shares system memory), the processor is steadier", "{0} là đồ họa tích hợp (dùng chung bộ nhớ hệ thống), bộ xử lý ổn định hơn"),
        ("gf_no_vulkan", "no Vulkan graphics driver is installed", "chưa cài trình điều khiển đồ họa Vulkan"),
        ("gf_small", "{0} has {1} of its own memory, {2} needs about {3}", "{0} có {1} bộ nhớ riêng, {2} cần khoảng {3}"),
        ("gf_ok", "{0} has {1} of its own memory", "{0} có {1} bộ nhớ riêng"),
        ("step_who", "Who: {0}", "Ai: {0}"),
        ("step_when", "When: {0}", "Khi nào: {0}"),
        ("coach_now_uses", "The coach now uses {0}.", "Trợ lý giờ dùng {0}."),
        ("coach_keeps_using", "The coach keeps using {0}: {1}", "Trợ lý tiếp tục dùng {0}: {1}"),
        ("coach_listening", "Coach listening ({0}).", "Trợ lý đang nghe ({0})."),
        ("coach_off_kept", "Coach off: nothing is sent. Its notes so far are kept.", "Trợ lý đã tắt: không gửi gì đi. Các ghi chú đến giờ vẫn được giữ."),
        ("coach_listening_again", "Coach listening again.", "Trợ lý nghe lại."),
        ("coach_cap", "Coach paused: it reached {0} calls for this recording (the cap).", "Trợ lý tạm dừng: đã đạt {0} lượt gọi cho bản ghi âm này (giới hạn)."),
        ("coach_no_answer", "no answer in {0} s", "không có trả lời sau {0} giây"),
        ("coach_retry", "Coach: {0}. Trying again in {1} s; recording and live text go on.", "Trợ lý: {0}. Thử lại sau {1} giây; ghi âm và phụ đề vẫn tiếp tục."),
        ("coach_last_answer", "Coach: last answer at {0}.", "Trợ lý: trả lời gần nhất lúc {0}."),
        ("coach_stopped_running", "Coach stopped; its last call was still running and is not saved.", "Trợ lý đã dừng; lượt gọi cuối vẫn đang chạy nên không được lưu."),
        ("coach_stopped", "Coach stopped with the recording.", "Trợ lý đã dừng cùng bản ghi âm."),
        ("toast_title", "RecBox coach", "Trợ lý RecBox"),
        ("coach_shows_here", "The coach shows cues, answers and next steps here while you record.", "Trợ lý hiện gợi ý, câu trả lời và việc tiếp theo ở đây khi bạn ghi âm."),
        ("coach_needs_live", "The coach reads the live text: turn on Live text while recording to use it.", "Trợ lý đọc phụ đề trực tiếp: hãy bật Phụ đề trực tiếp khi ghi âm để dùng trợ lý."),
        ("coach_summary_off", "Off. Nothing is sent anywhere. Turn it on for live cues, answers and next steps.", "Đang tắt. Không gửi gì đi đâu. Bật lên để có gợi ý, câu trả lời và việc tiếp theo ngay trong cuộc họp."),
        ("coach_summary", "{0} through {1}.", "{0} qua {1}."),
        ("coach_starts_with_live", "The coach starts with the live text.", "Trợ lý bắt đầu cùng phụ đề trực tiếp."),
        ("coach_is_off", "The coach is off.", "Trợ lý đang tắt."),
        ("coach_not_started", "The coach could not start.", "Trợ lý không khởi động được."),
        ("coach_start_failed", "The coach could not start: {0}. The recording goes on.", "Trợ lý không khởi động được: {0}. Việc ghi âm vẫn tiếp tục."),
        ("coach_saved", "Coach notes saved as coach.md beside the recording ({0} cue(s), {1} answer(s)).", "Đã lưu ghi chú của trợ lý vào coach.md cạnh bản ghi âm ({0} gợi ý, {1} câu trả lời)."),
        ("dlg_brief", "Meeting brief", "Ghi chú chuẩn bị cuộc họp"),
        ("dlg_brief_filter", "Text or Markdown|*.md;*.txt|All files|*.*", "Văn bản hoặc Markdown|*.md;*.txt|Mọi tệp|*.*"),
        ("brief_read_failed", "Could not read the brief: {0}", "Không đọc được ghi chú chuẩn bị: {0}"),

        // ---------- translate ----------
        ("translate_off", "Off", "Tắt"),
        ("translate_empty_reply", "The AI answered without any translated line.", "AI trả lời mà không có câu dịch nào."),
        ("translate_retry", "Translation: {0}. Trying again in {1} s; recording and live text go on.", "Dịch: {0}. Thử lại sau {1} giây; ghi âm và phụ đề vẫn tiếp tục."),
        ("translate_no_transcript", "No transcript.md to translate.", "Không có transcript.md để dịch."),
        ("translate_cannot_start", "Translation could not start: {0}", "Không bắt đầu dịch được: {0}"),
        ("translate_by_local", "Translated by {0} on this PC. Nothing leaves your computer.", "Dịch bằng {0} trên máy này. Không có gì rời khỏi máy tính của bạn."),
        ("translate_by", "Translated through {0}: the lines to translate are sent there.", "Dịch qua {0}: các câu cần dịch được gửi tới đó."),
        ("translate_live_usage", "{0} line(s) translated, {1} tokens in, {2} out", "Đã dịch {0} dòng, {1} token vào, {2} token ra"),
        ("translate_english", "English is translated offline by the speech model on this PC.", "Tiếng Anh được dịch ngoại tuyến bằng mô hình giọng nói trên máy này."),
        ("translate_needs_ai", "Translating to {0} needs an AI tool, and none is ready. Pick one below.", "Dịch sang {0} cần một công cụ AI, nhưng chưa có công cụ nào sẵn sàng. Hãy chọn bên dưới."),
        ("translate_transcript_progress", "Translating the transcript to {0}: {1} of {2} lines", "Đang dịch bản chép lời sang {0}: {1}/{2} dòng"),
        ("translate_transcript_done", "Translated transcript ready: {0}", "Bản chép lời đã dịch xong: {0}"),
        ("translate_transcript_failed", "Translated transcript not finished: {0}", "Chưa dịch xong bản chép lời: {0}"),

        // ---------- languages ----------
        ("lang_en", "English", "Tiếng Anh"),
        ("lang_vi", "Vietnamese", "Tiếng Việt"),
        ("lang_ja", "Japanese", "Tiếng Nhật"),
        ("lang_ko", "Korean", "Tiếng Hàn"),
        ("lang_zh", "Chinese", "Tiếng Trung"),
        ("lang_fr", "French", "Tiếng Pháp"),
        ("lang_de", "German", "Tiếng Đức"),
        ("lang_es", "Spanish", "Tiếng Tây Ban Nha"),
        ("lang_auto", "Auto", "Tự động"),
        ("lang_auto_choice", "Auto (detect, then keep)", "Tự động (nhận ra rồi giữ nguyên)"),
        ("lang_or", "{0} or {1}", "{0} hoặc {1}"),
        ("lang_detected", "{0} (detected)", "{0} (đã nhận ra)"),
        ("lang_detecting", "{0} (detecting)", "{0} (đang nhận ra)"),
        ("lang_listening", "Auto: listening for the language", "Tự động: đang nghe để nhận ra ngôn ngữ"),
        ("word_or", "or", "hoặc"),
        ("picked_live", "Picked: {0}", "Đã chọn: {0}"),
        ("using_live", "Using {0}", "Đang dùng {0}"),
        ("last_detected", "Last recording: {0} (detected). Auto listens again next time.", "Lần ghi trước: {0} (đã nhận ra). Lần sau chế độ Tự động sẽ nghe lại."),
        ("auto_listens", "Listens to the first minute of speech, picks the language, then keeps it.", "Nghe phút nói đầu tiên, chọn ngôn ngữ, rồi giữ nguyên."),
        ("anyof_text", "Each piece of speech is written in whichever it is: {0}.", "Mỗi đoạn lời nói được ghi theo đúng ngôn ngữ của nó: {0}."),
        ("mixed_text", "{0}, with {1} for speech that is clearly in it.", "{0}, và {1} cho những đoạn nói rõ ràng bằng ngôn ngữ đó."),
        ("single_text", "Everything is written as {0}.", "Mọi thứ được ghi bằng {0}."),
        ("ui_lang_en", "English", "English"),
        ("ui_lang_vi", "Tiếng Việt", "Tiếng Việt"),
        ("ui_follow_windows", "Follow Windows", "Theo Windows"),

        // ---------- speech engine, live text, transcript ----------
        ("where_named", "the {0}", "{0}"),
        ("where_gpu", "the graphics card", "card đồ họa"),
        ("where_cpu", "the processor", "bộ xử lý"),
        ("where_cpu_cores", "the processor ({0} cores)", "bộ xử lý ({0} nhân)"),
        ("engine_on", "{0} on {1}", "{0} trên {1}"),
        ("lag_live", "Live", "Trực tiếp"),
        ("lag_behind", "~{0} s behind", "chậm ~{0} giây"),
        ("lag_off", "Off", "Tắt"),
        ("lag_stopped", "Stopped", "Đã dừng"),
        ("lag_not_recording", "Not recording", "Chưa ghi âm"),
        ("lag_waiting_model", "Waiting for the model", "Đang chờ mô hình"),
        ("live_on", "Live text: {0} on {1}", "Phụ đề trực tiếp: {0} trên {1}"),
        ("live_loading", "Live text: loading {0} on {1}", "Phụ đề trực tiếp: đang nạp {0} trên {1}"),
        ("live_text_is", "Live text: {0}", "Phụ đề trực tiếp: {0}"),
        ("live_stopped_error", "Live text stopped: {0}. The recording goes on.", "Phụ đề trực tiếp đã dừng: {0}. Việc ghi âm vẫn tiếp tục."),
        ("live_far_behind", "Far behind ({0}): skipped {1} s of speech to catch up", "Chậm nhiều ({0}): bỏ qua {1} giây lời nói để bắt kịp"),
        ("live_catching_up", "Catching up ({0}): {1} pieces of speech decoded together", "Đang bắt kịp ({0}): giải mã {1} đoạn lời nói cùng lúc"),
        ("live_crashed_stop", "Live text stopped: the speech engine crashed {0} times in a minute ({1}). The recording goes on, and the full transcript still runs after Stop.", "Phụ đề trực tiếp đã dừng: bộ nhận dạng giọng nói bị lỗi {0} lần trong một phút ({1}). Việc ghi âm vẫn tiếp tục, và bản chép lời đầy đủ vẫn chạy sau khi Dừng."),
        ("live_crashed_restarted", "Live text: the speech engine crashed and was restarted ({0})", "Phụ đề trực tiếp: bộ nhận dạng giọng nói bị lỗi và đã được khởi động lại ({0})"),
        ("live_shows_here", "Live text shows here while you record.", "Phụ đề trực tiếp hiện ở đây khi bạn ghi âm."),
        ("live_on_again", "Live text turned on again.", "Đã bật lại phụ đề trực tiếp."),
        ("live_no_model", "Live text could not start: its model is not downloaded yet. The recording goes on.", "Phụ đề trực tiếp chưa chạy được: mô hình của nó chưa được tải về. Việc ghi âm vẫn tiếp tục."),
        ("live_turned_off", "Live text turned off. The lines so far are saved with the recording.", "Đã tắt phụ đề trực tiếp. Các dòng đến giờ được lưu cùng bản ghi âm."),
        ("live_downloading", "Downloading {0} for live text (once)...", "Đang tải {0} cho phụ đề trực tiếp (một lần)..."),
        ("live_start_failed", "Live text could not start: {0}. The recording goes on.", "Phụ đề trực tiếp không chạy được: {0}. Việc ghi âm vẫn tiếp tục."),
        ("live_saved", "Saved {0} live line(s) as live-transcript.txt beside the recording.", "Đã lưu {0} dòng phụ đề vào live-transcript.txt cạnh bản ghi âm."),
        ("who_you", "You", "Bạn"),
        ("who_others", "Others", "Người khác"),
        ("who_echo", "(echo?)", "(tiếng vọng?)"),
        ("copied_lines", "Copied {0} line(s).", "Đã sao chép {0} dòng."),
        ("copy_failed", "Could not copy: {0}", "Không sao chép được: {0}"),
        ("tx_preparing", "Preparing audio for transcription", "Đang chuẩn bị âm thanh để chép lời"),
        ("tx_track_mic", "microphone", "micrô"),
        ("tx_track_speaker", "system audio", "âm thanh máy tính"),
        ("tx_transcribing", "Transcribing {0}", "Đang chép lời {0}"),
        ("tx_transcribing_dir", "Transcribing {0}", "Đang chép lời {0}"),
        ("tx_gpu_failed", "The graphics card failed; transcribing on the processor", "Card đồ họa bị lỗi; đang chép lời bằng bộ xử lý"),
        ("tx_gpu_failed_short", "The graphics card (Vulkan) failed.", "Card đồ họa (Vulkan) bị lỗi."),
        ("tx_queued", "Transcript queued: it starts when the one before it is done. You can record again.", "Bản chép lời đang xếp hàng: sẽ bắt đầu khi bản trước xong. Bạn có thể ghi âm tiếp."),
        ("tx_background", "Transcribing in the background. You can record again.", "Đang chép lời ở chế độ nền. Bạn có thể ghi âm tiếp."),
        ("tx_ready", "Transcript ready: transcript.md ({0} s on {1}, {2})", "Bản chép lời đã xong: transcript.md ({0} giây trên {1}, {2})"),
        ("tx_not_finished", "Transcription did not finish.", "Việc chép lời chưa hoàn tất."),
        ("tx_cancelling", "Cancelling", "Đang hủy"),

        // ---------- main page states ----------
        ("status_ready", "Ready", "Sẵn sàng"),
        ("status_recording", "Recording", "Đang ghi âm"),
        ("status_saving", "Saving", "Đang lưu"),
        ("status_saved", "Saved", "Đã lưu"),
        ("hint_start", "Press Space or click to start", "Nhấn phím Cách hoặc bấm để bắt đầu"),
        ("hint_again", "Press Space or click to record again", "Nhấn phím Cách hoặc bấm để ghi âm lần nữa"),
        ("hint_writing", "Writing your files", "Đang ghi các tệp của bạn"),
        ("hint_saving_live", "Saving the live text", "Đang lưu phụ đề trực tiếp"),
        ("record_start", "Start recording", "Bắt đầu ghi âm"),
        ("record_stop", "Stop recording", "Dừng ghi âm"),
        ("src_mic_short", "Mic: {0}", "Micrô: {0}"),
        ("src_audio_short", "Audio: {0}", "Âm thanh: {0}"),
        ("device_default", "Default", "Mặc định"),
        ("device_default_is", "Default ({0})", "Mặc định ({0})"),
        ("no_mic_found", "No microphone found", "Không tìm thấy micrô"),
        ("no_speaker_found", "No speakers or headphones found", "Không tìm thấy loa hoặc tai nghe"),
        ("mic_quiet", "Mic is very quiet. Check it is the right device and is not muted.", "Micrô rất nhỏ. Hãy kiểm tra đúng thiết bị và không bị tắt tiếng."),
        ("start_failed", "Could not start recording: {0}", "Không bắt đầu ghi âm được: {0}"),
        ("save_failed", "The recording could not be saved completely: {0}. Raw parts are in {1}", "Không lưu được trọn vẹn bản ghi âm: {0}. Các phần thô nằm trong {1}"),
        ("settings_not_saved", "Settings could not be saved: {0}", "Không lưu được cài đặt: {0}"),
        ("saved_title", "Saved {0}", "Đã lưu {0}"),
        ("saved_detail", "{0}  ·  {1} audio and text files  ·  session.json describes them", "{0}  ·  {1} tệp âm thanh và văn bản  ·  session.json mô tả chúng"),
        ("recent_mic", "mic", "micrô"),
        ("recent_speaker", "system audio", "âm thanh máy tính"),
        ("recent_transcript", "transcript", "bản chép lời"),
        ("path_copied", "Folder path copied. Paste it into your LLM tool or file picker.", "Đã sao chép đường dẫn thư mục. Hãy dán vào công cụ LLM hoặc hộp chọn tệp."),
        ("no_session_json", "That folder has no session.json, so RecBox cannot transcribe it.", "Thư mục đó không có session.json nên RecBox không chép lời được."),
        ("dlg_save_to", "Save recordings to", "Lưu bản ghi âm vào"),
        ("gpu_turned_off", "Turned off by RecBox: {0}", "RecBox đã tắt: {0}"),
        ("model_auto_uses", "Auto uses {0} on this PC.", "Tự động dùng {0} trên máy này."),
        ("model_ready", "Downloaded and ready.", "Đã tải về và sẵn sàng."),
        ("model_missing", "Not downloaded yet. One download, then it works offline.", "Chưa tải về. Tải một lần, sau đó chạy ngoại tuyến."),
        ("model_until", "{0} is used until then.", "Trong lúc chờ sẽ dùng {0}."),
        ("download_failed", "Download failed: {0}. Check the internet connection and try again.", "Tải về thất bại: {0}. Hãy kiểm tra kết nối mạng và thử lại."),
        ("diag_collecting", "Collecting logs", "Đang thu thập nhật ký"),
        ("diag_copied", "Copied {0}. Paste it into a chat or an email. No recordings are in it.", "Đã sao chép {0}. Hãy dán vào khung chat hoặc email. Trong đó không có bản ghi âm nào."),
        ("diag_saved", "Saved {0}. Send that file.", "Đã lưu {0}. Hãy gửi tệp đó."),
        ("diag_failed", "Could not collect the logs: {0}. Use Open log folder instead.", "Không thu thập được nhật ký: {0}. Hãy dùng Mở thư mục nhật ký."),
        ("crash_box", "The details are in {0}.\nIn Settings, Copy diagnostics makes a file you can send.", "Chi tiết nằm trong {0}.\nTrong Cài đặt, Sao chép chẩn đoán tạo một tệp bạn có thể gửi."),

        // ---------- updates ----------
        ("upd_checking", "Checking for updates", "Đang kiểm tra cập nhật"),
        ("upd_no_feed", "No update feed is set for this build.", "Bản dựng này chưa đặt nguồn cập nhật."),
        ("upd_latest", "You have the latest version ({0}).", "Bạn đang dùng bản mới nhất ({0})."),
        ("upd_ready", "Version {0} is ready. Restart to finish.", "Phiên bản {0} đã sẵn sàng. Khởi động lại để hoàn tất."),
        ("upd_unreachable", "Could not reach the update server. Recording still works; try again later.", "Không kết nối được máy chủ cập nhật. Vẫn ghi âm bình thường; hãy thử lại sau."),
        ("upd_installed_only", "Updates apply to installed copies (run Setup.exe).", "Cập nhật chỉ áp dụng cho bản đã cài (chạy Setup.exe)."),
        ("upd_new_on_page", "New version {0} available. Download it from the release page.", "Có phiên bản mới {0}. Hãy tải từ trang phát hành."),
        ("upd_github_unreachable", "Could not reach GitHub. Recording still works; try again later.", "Không kết nối được GitHub. Vẫn ghi âm bình thường; hãy thử lại sau."),
        ("upd_stop_first", "Stop the recording first, then restart to update.", "Hãy dừng ghi âm trước, rồi khởi động lại để cập nhật."),
        ("upd_saving_first", "Saving the recording first, then RecBox restarts to update.", "Đang lưu bản ghi âm trước, sau đó RecBox khởi động lại để cập nhật."),

        // ---------- the screenshot preview (made up meeting) ----------
        ("preview_goal", "Agree on the retry fix and a release date", "Thống nhất bản sửa lỗi thử lại và ngày phát hành"),
        ("preview_role", "Backend developer presenting the fix", "Lập trình viên backend trình bày bản sửa"),
        ("preview_brief", "Me: Lan\nThem: Minh\nQuestions to ask:\n- When can we ship to staging?\n- Who tests the lost-connection case?", "Me: Lan\nThem: Minh\nCâu cần hỏi:\n- Khi nào đưa lên staging được?\n- Ai kiểm thử trường hợp mất kết nối?"),
        ("preview_cue", "Ask Minh who tests the lost-connection case before you demo.", "Hỏi Minh ai kiểm thử trường hợp mất kết nối trước khi bạn demo."),
        ("preview_q1", "When can we ship to staging?", "Khi nào đưa lên staging được?"),
        ("preview_a1", "Friday, after the retry demo (Minh, 00:12:05)", "Thứ Sáu, sau buổi demo thử lại (Minh, 00:12:05)"),
        ("preview_q2", "Who tests the lost-connection case?", "Ai kiểm thử trường hợp mất kết nối?"),
        ("preview_next", "Demo the retry with the connection cut", "Demo việc thử lại khi ngắt kết nối"),
        ("preview_when", "today", "hôm nay"),
        ("preview_note1", "Minh wants to review the retry path of outgoing calls.", "Minh muốn xem lại luồng thử lại của các cuộc gọi đi."),
        ("preview_note2", "Lan says the retry fix follows last week's review.", "Lan nói bản sửa lỗi thử lại làm theo buổi rà soát tuần trước."),

        // ---------- the window (MainWindow.xaml) ----------
        ("x_ui_switch_tip", "Interface language: one click, applies at once", "Ngôn ngữ giao diện: một lần bấm, áp dụng ngay"),
        ("x_live_text", "Live text", "Phụ đề"),
        ("x_live_text_tip", "Show or hide what is said, beside this page", "Hiện hoặc ẩn lời đang nói, bên cạnh trang này"),
        ("x_live_text_name", "Show or hide live text", "Hiện hoặc ẩn phụ đề trực tiếp"),
        ("x_settings_tip", "Settings: folder, models, updates, help", "Cài đặt: thư mục, mô hình, cập nhật, trợ giúp"),
        ("x_settings", "Settings", "Cài đặt"),
        ("x_could_not_record", "Could not record", "Không ghi âm được"),
        ("x_writing_transcript", "Writing the transcript", "Đang viết bản chép lời"),
        ("x_record_meanwhile", "You can record again meanwhile.", "Trong lúc đó bạn vẫn có thể ghi âm."),
        ("x_cancel", "Cancel", "Hủy"),
        ("x_cancel_tip", "Stop writing this transcript (the recording is kept)", "Dừng viết bản chép lời này (bản ghi âm vẫn được giữ)"),
        ("x_this_recording", "This recording", "Bản ghi âm này"),
        ("x_mic", "Microphone", "Micrô"),
        ("x_mic_sub", "Your voice", "Giọng của bạn"),
        ("x_mic_device", "Microphone device", "Thiết bị micrô"),
        ("x_mic_tip", "Default follows Windows: plug in a headset and RecBox switches with it", "Mặc định theo Windows: cắm tai nghe vào là RecBox chuyển theo"),
        ("x_record_mic", "Record microphone", "Ghi âm micrô"),
        ("x_sys", "System audio", "Âm thanh máy tính"),
        ("x_sys_sub", "Everything your PC plays: the other side of a call, videos", "Mọi thứ máy tính phát ra: phía bên kia cuộc gọi, video"),
        ("x_sys_device", "Playback device", "Thiết bị phát"),
        ("x_sys_tip", "Default follows Windows: switch speakers or headphones and RecBox switches with it", "Mặc định theo Windows: đổi loa hoặc tai nghe là RecBox chuyển theo"),
        ("x_record_sys", "Record system audio", "Ghi âm thanh máy tính"),
        ("x_spoken", "Spoken language", "Ngôn ngữ nói"),
        ("x_spoken_tip", "Live text uses a change from the next piece of speech; the transcript uses what is set at Stop", "Phụ đề áp dụng thay đổi từ đoạn nói kế tiếp; bản chép lời dùng lựa chọn lúc bấm Dừng"),
        ("x_transcript_after", "Transcript after Stop", "Chép lời sau khi Dừng"),
        ("x_live_while", "Live text while recording", "Phụ đề trực tiếp khi ghi âm"),
        ("x_live_toggle_panel", "Show or hide the live text panel", "Hiện hoặc ẩn khung phụ đề"),
        ("x_coach", "Live coach", "Trợ lý cuộc họp"),
        ("x_coach_sends", "While the coach is on, the live text and your brief are sent to the AI tool you picked.", "Khi trợ lý bật, phụ đề trực tiếp và ghi chú chuẩn bị được gửi tới công cụ AI bạn chọn."),
        ("x_coach_setup_link", "Set up this meeting: goal, brief, style, AI", "Thiết lập cuộc họp: mục tiêu, ghi chú, phong cách, AI"),
        ("x_goal", "Goal of this meeting", "Mục tiêu của cuộc họp"),
        ("x_goal_ph", "For example: agree on the price and a start date", "Ví dụ: thống nhất giá và ngày bắt đầu"),
        ("x_role", "Your role", "Vai trò của bạn"),
        ("x_role_ph", "For example: the seller, the candidate, the team lead", "Ví dụ: người bán, ứng viên, trưởng nhóm"),
        ("x_brief", "Brief: who is who, questions to ask, facts", "Ghi chú chuẩn bị: ai là ai, câu cần hỏi, dữ kiện"),
        ("x_load_file", "Load a file", "Mở tệp"),
        ("x_brief_ph", "Me: your name\nThem: their names\nQuestions to ask:\n- ...", "Me: tên bạn\nThem: tên của họ\nCâu cần hỏi:\n- ..."),
        ("x_brief_name", "Meeting brief", "Ghi chú chuẩn bị cuộc họp"),
        ("x_style", "Style", "Phong cách"),
        ("x_style_name", "Coach style", "Phong cách trợ lý"),
        ("x_instr_ph", "Anything else, in your words: for example, remind me to ask about the budget", "Điều gì khác, theo lời của bạn: ví dụ, nhắc tôi hỏi về ngân sách"),
        ("x_instr_name", "Your own instructions", "Hướng dẫn riêng của bạn"),
        ("x_coach_writes", "The coach writes in", "Trợ lý viết bằng"),
        ("x_coach_lang_name", "Coach language", "Ngôn ngữ của trợ lý"),
        ("x_ai", "AI tool", "Công cụ AI"),
        ("x_ai_sub", "An AI tool installed on this PC, with your own sign-in. RecBox never calls a cloud API with a key.", "Một công cụ AI cài trên máy này, dùng tài khoản của bạn. RecBox không bao giờ gọi API đám mây bằng khóa."),
        ("x_ai_name", "AI tool for the coach and translation", "Công cụ AI cho trợ lý và bản dịch"),
        ("x_model", "Model", "Mô hình"),
        ("x_model_name", "AI model", "Mô hình AI"),
        ("x_model_default_ph", "Empty: the tool's own default", "Để trống: mặc định của công cụ"),
        ("x_address", "Address (this PC only)", "Địa chỉ (chỉ trên máy này)"),
        ("x_address_name", "AI address", "Địa chỉ AI"),
        ("x_model_name_label", "Model name", "Tên mô hình"),
        ("x_endpoint_model_name", "AI model name", "Tên mô hình AI"),
        ("x_slides", "Save slides from the meeting window", "Lưu trang chiếu từ cửa sổ cuộc họp"),
        ("x_slides_sub", "A picture each time the shared screen changes, in a slides folder beside the recording. Stays on this PC.", "Một ảnh mỗi khi màn hình chia sẻ thay đổi, trong thư mục slides cạnh bản ghi âm. Chỉ ở trên máy này."),
        ("x_slides_ph", "Words in the window title, for example Meet, Zoom or Teams", "Từ trong tiêu đề cửa sổ, ví dụ Meet, Zoom hoặc Teams"),
        ("x_slides_name", "Meeting window title", "Tiêu đề cửa sổ cuộc họp"),
        ("x_translate", "Translate to", "Dịch sang"),
        ("x_translate_sub", "Live text and the transcript, with the original kept", "Phụ đề và bản chép lời, vẫn giữ bản gốc"),
        ("x_translate_tip", "English is translated offline; other languages go through the AI tool picked for the coach", "Tiếng Anh được dịch ngoại tuyến; các ngôn ngữ khác đi qua công cụ AI đã chọn cho trợ lý"),
        ("x_translate_pick_ai", "Pick the AI tool that translates:", "Chọn công cụ AI để dịch:"),
        ("x_more_ai", "More AI choices (model, address)", "Thêm lựa chọn AI (mô hình, địa chỉ)"),
        ("x_open", "Open", "Mở"),
        ("x_open_tip", "Open the folder with the files", "Mở thư mục chứa các tệp"),
        ("x_copy_path", "Copy folder path", "Sao chép đường dẫn thư mục"),
        ("x_again_tip", "Write the transcript of this recording again", "Viết lại bản chép lời của bản ghi âm này"),
        ("x_again_name", "Transcribe again", "Chép lời lại"),
        ("x_recent", "Recent", "Gần đây"),
        ("x_all_recordings", "All recordings", "Tất cả bản ghi âm"),
        ("x_recent_empty", "Your recordings will show up here. Each one is a folder with WAV files and a session.json that explains them.", "Các bản ghi âm sẽ hiện ở đây. Mỗi bản là một thư mục chứa tệp WAV và một session.json mô tả chúng."),
        ("x_back", "Back", "Quay lại"),
        ("x_settings_note", "What you set for each recording (language, transcript, live text, translate) is on the main page.", "Các lựa chọn cho từng bản ghi âm (ngôn ngữ, chép lời, phụ đề, dịch) nằm ở trang chính."),
        ("x_interface", "Interface", "Giao diện"),
        ("x_ui_language", "Interface language", "Ngôn ngữ giao diện"),
        ("x_ui_language_sub", "Follow Windows shows Vietnamese when Windows is in Vietnamese, else English. The switch at the top changes it in one click.", "Theo Windows sẽ hiện tiếng Việt khi Windows dùng tiếng Việt, nếu không thì tiếng Anh. Nút chuyển ở trên cùng đổi chỉ bằng một lần bấm."),
        ("x_default_translate", "Translate to, for each new recording", "Dịch sang, cho mỗi bản ghi âm mới"),
        ("x_default_translate_sub", "The main page can change it for one recording.", "Trang chính có thể đổi riêng cho một bản ghi âm."),
        ("x_files", "Files", "Tệp"),
        ("x_save_to", "Save recordings to", "Lưu bản ghi âm vào"),
        ("x_change", "Change", "Đổi"),
        ("x_speech_copies", "Speech copies (16 kHz mono)", "Bản giọng nói (16 kHz mono)"),
        ("x_speech_copies_sub", "Small extra files that speech-to-text tools and LLMs read directly", "Các tệp phụ nhỏ mà công cụ chuyển giọng nói thành chữ và LLM đọc trực tiếp"),
        ("x_mixed", "Mixed track", "Rãnh trộn"),
        ("x_mixed_sub", "One file with both sides, when microphone and system audio are both on", "Một tệp chứa cả hai phía, khi bật cả micrô và âm thanh máy tính"),
        ("x_clean", "Clean up speech copies", "Làm sạch bản giọng nói"),
        ("x_clean_sub", "Filters hum and raises the peak of the 16 kHz files to -1 dBFS, by at most 12 dB. The full-quality files stay raw.", "Lọc tiếng ù và nâng đỉnh của các tệp 16 kHz lên -1 dBFS, tối đa 12 dB. Các tệp chất lượng đầy đủ vẫn giữ nguyên."),
        ("x_boost", "Boost mic to maximum while recording", "Tăng micrô tối đa khi ghi âm"),
        ("x_boost_sub", "Unmutes the mic and raises its Windows volume and hardware boost. Everything goes back when the recording stops. Applies from the next recording.", "Bỏ tắt tiếng micrô và nâng âm lượng Windows cùng mức tăng phần cứng. Mọi thứ trở lại như cũ khi dừng ghi âm. Áp dụng từ bản ghi âm sau."),
        ("x_models", "Speech models", "Mô hình giọng nói"),
        ("model_auto", "Auto (recommended)", "Tự động (khuyên dùng)"),
        ("model_auto_detail", "Picks the model for this PC: memory, graphics card, processor", "Tự chọn mô hình hợp với máy này: bộ nhớ, card đồ họa, bộ xử lý"),
        ("model_tiny_detail", "Fastest. 31 MB download, for older laptops and live text", "Nhanh nhất. Tải về 31 MB, cho laptop cũ và phụ đề trực tiếp"),
        ("model_base_detail", "Fast. 60 MB download, runs on any PC", "Nhanh. Tải về 60 MB, chạy được trên mọi máy"),
        ("model_small_detail", "More accurate. 190 MB download", "Chính xác hơn. Tải về 190 MB"),
        ("model_medium_detail", "Accurate. 539 MB download, for a fast processor", "Chính xác. Tải về 539 MB, cho bộ xử lý mạnh"),
        ("model_large-v3-turbo_detail", "Most accurate. 574 MB download, best with a graphics card", "Chính xác nhất. Tải về 574 MB, tốt nhất khi có card đồ họa"),
        ("x_model_transcript", "Model for the transcript", "Mô hình cho bản chép lời"),
        ("x_speech_model", "Speech model", "Mô hình giọng nói"),
        ("x_download", "Download", "Tải về"),
        ("x_live_best", "Live text always uses the best model this PC keeps up with.", "Phụ đề trực tiếp luôn dùng mô hình tốt nhất mà máy này theo kịp."),
        ("x_gpu", "Use the graphics card", "Dùng card đồ họa"),
        ("x_gpu_sub", "Faster when available. Falls back to the processor on its own.", "Nhanh hơn khi có. Tự chuyển về bộ xử lý khi cần."),
        ("x_updates", "Updates", "Cập nhật"),
        ("x_check_start", "Check for updates when RecBox starts", "Kiểm tra cập nhật khi RecBox khởi động"),
        ("x_check_now", "Check now", "Kiểm tra ngay"),
        ("x_restart_update", "Restart to update", "Khởi động lại để cập nhật"),
        ("x_release_page", "Open the release page", "Mở trang phát hành"),
        ("x_help", "Help and diagnostics", "Trợ giúp và chẩn đoán"),
        ("x_wrong", "Something went wrong?", "Có gì trục trặc?"),
        ("x_diag_sub", "Copy diagnostics makes one zip with the logs, settings and PC details (no recordings, no meeting brief) to send to whoever helps you.", "Sao chép chẩn đoán tạo một tệp zip gồm nhật ký, cài đặt và thông tin máy (không có bản ghi âm, không có ghi chú cuộc họp) để gửi cho người giúp bạn."),
        ("x_open_logs", "Open log folder", "Mở thư mục nhật ký"),
        ("x_copy_diag", "Copy diagnostics", "Sao chép chẩn đoán"),
        ("x_drag_resize", "Drag to resize the live text", "Kéo để đổi cỡ khung phụ đề"),
        ("x_show_live", "Show the live text", "Hiện phụ đề"),
        ("x_coach_tab", "Coach", "Trợ lý"),
        ("x_show_coach", "Show the coach", "Hiện trợ lý"),
        ("x_copy_live", "Copy all live text", "Sao chép toàn bộ phụ đề"),
        ("x_hide_live_tip", "Hide live text (it keeps running)", "Ẩn phụ đề (vẫn tiếp tục chạy)"),
        ("x_hide_live", "Hide live text", "Ẩn phụ đề"),
        ("x_live_lang_name", "Spoken language for live text", "Ngôn ngữ nói cho phụ đề"),
        ("x_applies_next", "Applies from the next piece of speech", "Áp dụng từ đoạn nói kế tiếp"),
        ("x_on", "On", "Bật"),
        ("x_live_onoff", "Live text on or off", "Bật hoặc tắt phụ đề"),
        ("x_coach_onoff", "Live coach on or off", "Bật hoặc tắt trợ lý"),
        ("x_setup_meeting", "Set up this meeting", "Thiết lập cuộc họp này"),
        ("x_say_now", "Say or ask now", "Nói hoặc hỏi ngay"),
        ("x_coach_empty", "The coach listens to the live text and writes here: what to say next, answers to your brief's questions, next steps and notes. At most one pop-up every 3 minutes.", "Trợ lý nghe phụ đề trực tiếp và viết ở đây: nên nói gì tiếp, câu trả lời cho các câu hỏi trong ghi chú chuẩn bị, việc tiếp theo và ghi chú. Tối đa một thông báo mỗi 3 phút."),
        ("x_questions", "Questions", "Câu hỏi"),
        ("x_not_answered", "Not answered yet", "Chưa có câu trả lời"),
        ("x_next_steps", "Next steps", "Việc tiếp theo"),
        ("x_attention", "Needs your attention", "Cần bạn chú ý"),
        ("x_earlier", "Earlier cues", "Gợi ý trước đó"),
        ("x_notes", "Notes", "Ghi chú"),
        ("x_live_empty", "Live text appears here while you record. You = your microphone, Others = the system audio (the other side of a call). [?] marks words the model was unsure of.", "Phụ đề hiện ở đây khi bạn ghi âm. Bạn = micrô của bạn, Người khác = âm thanh máy tính (phía bên kia cuộc gọi). [?] đánh dấu từ mà mô hình chưa chắc."),
        ("x_jump", "Jump to latest", "Đến dòng mới nhất"),
        ("x_jump_name", "Jump to the latest live text", "Đến phụ đề mới nhất"),
        ("x_update_ready", "Update ready", "Bản cập nhật đã sẵn sàng"),
        ("x_update_restart", "Restart RecBox to finish updating.", "Khởi động lại RecBox để hoàn tất cập nhật."),
    };

    /// <summary>Key to (English, Vietnamese). A key written twice is a mistake: it throws at startup and in the tests.</summary>
    public static readonly IReadOnlyDictionary<string, (string En, string Vi)> Table = Build();

    private static Dictionary<string, (string En, string Vi)> Build()
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var (key, en, vi) in Rows)
            if (!d.TryAdd(key, (en, vi))) throw new InvalidOperationException($"text key '{key}' is written twice");
        return d;
    }
}
