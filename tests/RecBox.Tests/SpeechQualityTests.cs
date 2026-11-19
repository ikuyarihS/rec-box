using RecBox.Core;
using RecBox.Core.Audio;
using RecBox.Core.Transcription;
using Xunit;

namespace RecBox.Tests;

/// <summary>
/// What makes the text good (v1.2.0): the discrete graphics card, one language per session by a vote, the kit's
/// filters, the repeat guard, the segmenter, the echo tag, the limiter that keeps the 16 kHz copies from clipping,
/// and a log that never stops silently.
/// </summary>
[Collection("serial")]
public sealed class SpeechQualityTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private const int Rate = KitSegmenter.Rate;
    private readonly string _home = Path.Combine(Path.GetTempPath(), "recbox-tests", "sq-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldHome = Environment.GetEnvironmentVariable("RECBOX_HOME");

    public SpeechQualityTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("RECBOX_HOME", _home);
    }

    public void Dispose()
    {
        Log.ResetFallbacks();
        Environment.SetEnvironmentVariable("RECBOX_HOME", _oldHome);
        try { Directory.Delete(_home, true); } catch { }
    }

    // ================= graphics card =================

    [Fact]
    public void DevicePick_IsTheDiscreteCardWithTheMostMemory_NeverTheIntegratedOne()
    {
        var iris = new VulkanDevice(0, "Intel(R) Iris(R) Xe Graphics", VulkanDeviceType.Integrated, 2 * Gb);
        var rtx = new VulkanDevice(1, "NVIDIA GeForce RTX 3060 Laptop GPU", VulkanDeviceType.Discrete, 6 * Gb);
        Assert.Equal(1, GpuDevicePick.Pick(new[] { iris, rtx })!.Index); // the 2026-11-19 meeting ran on device 0 = Iris Xe
        var small = new VulkanDevice(2, "NVIDIA GeForce MX450", VulkanDeviceType.Discrete, 2 * Gb);
        Assert.Equal(1, GpuDevicePick.Pick(new[] { iris, small, rtx })!.Index);
        Assert.Equal(0, GpuDevicePick.Pick(new[] { iris })!.Index); // integrated only when nothing else exists
        Assert.Null(GpuDevicePick.Pick(Array.Empty<VulkanDevice>()));
        Assert.Null(GpuDevicePick.Pick(new[] { new VulkanDevice(0, "llvmpipe", VulkanDeviceType.Cpu, 0) }));
        Assert.Contains("Vulkan1 = NVIDIA GeForce RTX 3060", rtx.Describe());
    }

    // ================= language =================

    [Fact]
    public void Language_Fixed_IsUsedForEveryChunk_AndNeverDetects()
    {
        var l = new LanguageLock("vi");
        Assert.True(l.IsLocked);
        Assert.False(l.NeedsDetection);
        Assert.Equal("vi", l.Next);
        Assert.False(l.Offer("en", 0.99f, 100));
        Assert.Equal("vi", l.Next);
        Assert.Equal("Vietnamese", l.Describe());
    }

    [Fact]
    public void Language_Auto_VotesOverTheFirstMinute_ThenLocksForTheSession()
    {
        var l = new LanguageLock("auto");
        Assert.Equal("auto", l.Next);
        Assert.True(l.NeedsDetection);
        Assert.Equal("Auto: listening for the language", l.Describe());
        Assert.False(l.Offer("vi", 0.9f, 1.0));
        Assert.Null(l.Leading); // under 1.5 s of speech
        Assert.False(l.Offer("vi", 0.9f, 2.0));
        Assert.Equal("vi", l.Next);
        Assert.Equal("Vietnamese (detecting)", l.Describe());
        // a confident wrong detection on the mic (half weight) does not take over
        Assert.False(l.Offer("en", 0.95f, 4.0, weight: 0.5));
        Assert.Equal("vi", l.Leading);
        var locked = false;
        for (var i = 0; i < 20 && !locked; i++) locked = l.Offer(i % 4 == 0 ? "ja" : "vi", 0.85f, 4.0);
        Assert.True(locked);
        Assert.Equal("vi", l.Detected);
        Assert.False(l.NeedsDetection);
        Assert.Equal("Vietnamese (detected)", l.Describe());
        Assert.False(l.Offer("en", 1f, 100)); // locked: never free detection per chunk
        Assert.Equal("vi", l.Next);
    }

    [Fact]
    public void Language_Auto_WithoutAClearWinner_VotesLonger_AndAChangeStartsOver()
    {
        var l = new LanguageLock("auto");
        var at = 0.0;
        var locked = false;
        while (!locked && at < 200)
        {
            locked = l.Offer(((int)(at / 5)) % 2 == 0 ? "vi" : "en", 0.9f, 5.0);
            at += 5;
        }
        Assert.True(locked);
        Assert.True(at >= 2 * LanguageLock.VoteSeconds, $"locked at {at} s");
        l.Change("en");
        Assert.Equal("en", l.Next);
        Assert.Null(l.Detected);
        l.Change("auto");
        Assert.Equal("auto", l.Next);
        Assert.True(l.NeedsDetection);
    }

    [Fact]
    public void Language_Mixed_ViEn_TakesEnglishOnlyWhenSure()
    {
        Assert.Equal("en", Languages.ChooseMixed("vi+en", "en", 0.85f));
        Assert.Equal("vi", Languages.ChooseMixed("vi+en", "en", 0.7f));
        Assert.Equal("vi", Languages.ChooseMixed("vi+en", "ja", 0.99f)); // not in the pair
        Assert.Equal("vi", Languages.ChooseMixed("vi+en", null, 0));
        Assert.Equal(new[] { "vi", "en" }, Languages.Allowed("vi+en", "vi"));
        Assert.Equal(new[] { "vi" }, Languages.Allowed("vi", "en"));
        Assert.Empty(Languages.Allowed("auto", "auto"));
        Assert.Contains(Languages.Choices, c => c.Code == Languages.ViEn);
    }

    [Fact]
    public void ModelPicker_NeverTinyOrBaseForVietnamese()
    {
        var weak = new HardwareProfile(4 * Gb, 2 * Gb, 2, 4, true, null, false);
        Assert.Equal("small", ModelPicker.Pick(weak, TranscriptionUse.Live, true, "vi").Model.Key);
        Assert.Equal("small", ModelPicker.Pick(weak, TranscriptionUse.Live, true, "auto").Model.Key);
        Assert.Equal("base", ModelPicker.Pick(weak, TranscriptionUse.Live, true, "en").Model.Key);
        Assert.NotNull(WhisperModels.Find("medium"));
    }

    // ================= filters =================

    private static WorkerSegment Seg(double start, double end, string text, double noSpeech = 0.05, double logprob = -0.3) =>
        new() { Start = start, End = end, Text = " " + text, NoSpeech = noSpeech, AvgLogprob = logprob };

    [Fact]
    public void Filters_DropTagsStockPhrasesAndOtherScripts_AndTagUnsureText()
    {
        var vi = new[] { "vi" };
        Assert.Null(TranscriptFilter.Filter(Seg(0, 2, "[Music]"), vi));
        Assert.Null(TranscriptFilter.Filter(Seg(0, 2, "Hãy đăng ký kênh để ủng hộ nhé"), vi));
        Assert.Null(TranscriptFilter.Filter(Seg(0, 2, "Bye bye!"), vi));
        Assert.Null(TranscriptFilter.Filter(Seg(0, 2, "Продолжение следует"), vi)); // Cyrillic in a Vietnamese meeting
        Assert.Null(TranscriptFilter.Filter(Seg(0, 2, "chào anh", noSpeech: 0.95, logprob: -1.5), vi)); // confident silence
        Assert.Equal("chào anh hôm nay", TranscriptFilter.Filter(Seg(0, 2, "chào anh [Music] hôm nay"), vi)!.Text);
        var unsure = TranscriptFilter.Filter(Seg(0, 2, "báo cáo tuần", logprob: -1.2), vi)!;
        Assert.True(unsure.Low);
        Assert.Equal("[?] báo cáo tuần", TranscriptFilter.Join(new[] { unsure }));
        Assert.Equal(Confidence.Keep, TranscriptFilter.Gate(0.1, -0.3));
        Assert.Equal(Confidence.Unsure, TranscriptFilter.Gate(0.7, -0.5));
        Assert.Equal(Confidence.Drop, TranscriptFilter.Gate(0.95, -1.2));
        Assert.True(TranscriptFilter.ScriptOk("xin chào các bạn", vi));
        Assert.True(TranscriptFilter.ScriptOk("こんにちは", new[] { "ja" }));
        Assert.False(TranscriptFilter.Hallucinated("bye bye everyone, the report is in the folder"));
    }

    [Fact]
    public void Filters_CollapseLoops_WithinALineAndAcrossPieces()
    {
        Assert.Equal("cảm ơn", TranscriptFilter.CollapseLoops("cảm ơn cảm ơn cảm ơn cảm ơn"));
        Assert.True(TranscriptFilter.HasLoop("ok ok ok ok"));
        Assert.Equal("anh nói đi", TranscriptFilter.CollapseLoops("anh nói đi"));
        var same = Enumerable.Range(0, 4).Select(_ => new Piece("vâng em hiểu rồi", false)).ToList();
        Assert.Equal("vâng em hiểu rồi", TranscriptFilter.Join(same));
    }

    [Fact]
    public void RepeatGuard_DropsTheModelLooping_KeepsRealShortReplies()
    {
        var g = new TranscriptFilter.RepeatGuard();
        Assert.False(g.IsRepeat("chúng ta bắt đầu cuộc họp", 10));
        Assert.True(g.IsRepeat("chúng ta bắt đầu cuộc họp", 14)); // the same 4+ words right after
        Assert.False(g.IsRepeat("vâng"));
        Assert.False(g.IsRepeat("vâng")); // a short reply twice is people talking
        Assert.True(g.IsRepeat("vâng")); // the third in a row is a loop
        var t = new TranscriptFilter.RepeatGuard();
        Assert.False(t.IsRepeat("anh gửi em file báo cáo nhé", 0));
        Assert.False(t.IsRepeat("dạ", 5));
        Assert.True(t.IsRepeat("anh gửi em file báo cáo nhé", 40)); // again within a minute
        Assert.False(t.IsRepeat("vâng ạ", 120));
        Assert.False(t.IsRepeat("anh gửi em file báo cáo nhé", 200)); // minutes later, after other lines: said again for real
    }

    [Fact]
    public void Transcript_Clean_NoLineMoreThanThreeTimes_AndEchoIsTaggedNotRemoved()
    {
        var mic = new List<WorkerSegment>();
        var speaker = new List<WorkerSegment>();
        // the model stuck on one sentence for 20 minutes (mic_16k of the field recording: 21 copies)
        for (var i = 0; i < 21; i++)
        {
            mic.Add(Seg(i * 70, i * 70 + 3, "Mình sẽ gửi lại tài liệu cho mọi người"));
            mic.Add(Seg(i * 70 + 10, i * 70 + 12, $"câu số {i} khác hẳn"));
        }
        speaker.Add(Seg(5000, 5004, "hôm nay chúng ta bàn về kế hoạch triển khai dự án"));
        mic.Add(Seg(5000.3, 5004.2, "hôm nay chúng ta bàn về kế hoạch triển khai dự án"));
        var lines = TranscriptionService.Clean(new[] { mic, speaker }, new[] { "You", "Others" }, "vi", out var dropped);
        var counts = lines.GroupBy(l => (l.Speaker, l.Text)).Max(g => g.Count());
        Assert.True(counts <= TranscriptFilter.MaxSameLine, $"a line appears {counts} times");
        Assert.True(dropped > 0);
        var echo = Assert.Single(lines, l => l.Echo);
        Assert.Equal("You", echo.Speaker);
        Assert.Contains(lines, l => l.Speaker == "Others" && l.Text.Contains("kế hoạch"));
    }

    // ================= echo =================

    [Fact]
    public void EchoTag_MicHearingTheSpeakers_IsTagged_OwnSpeechIsNot()
    {
        var remote = new[] { new TimedText(10, 14, "hôm nay chúng ta bàn về kế hoạch triển khai dự án") };
        Assert.True(EchoTag.Check(new TimedText(10.4, 14.3, "hôm nay chúng ta bàn về kế hoạch triển khai"), remote));
        Assert.False(EchoTag.Check(new TimedText(10.4, 14.3, "em thấy phương án hai tốt hơn chi phí thấp"), remote));
        Assert.False(EchoTag.Check(new TimedText(40, 44, "hôm nay chúng ta bàn về kế hoạch triển khai"), remote)); // not at the same time
    }

    // ================= segmenter =================

    /// <summary>A stand-in for Silero: speech where the signal is above 0.05, in 10 ms frames, gaps under 0.2 s bridged.</summary>
    private static IReadOnlyList<(int Start, int End)>? FakeSilero(float[] samples, int count)
    {
        const int frame = 160;
        var flags = new bool[count / frame];
        for (var f = 0; f < flags.Length; f++)
        {
            var peak = 0f;
            for (var i = 0; i < frame; i++) peak = Math.Max(peak, Math.Abs(samples[f * frame + i]));
            flags[f] = peak > 0.05f;
        }
        return EnergyVad.Spans(flags, frame, Rate, 0.2, 0.05, 0.25, count);
    }

    private static float[] Recording(double seconds, params (double From, double To)[] speech)
    {
        var x = new float[(int)(seconds * Rate)];
        var rng = new Random(7);
        for (var i = 0; i < x.Length; i++)
        {
            var t = i / (double)Rate;
            x[i] = (float)(rng.NextDouble() - 0.5) * 0.002f; // room noise
            if (speech.Any(s => t >= s.From && t < s.To)) x[i] += (float)(0.3 * Math.Sin(2 * Math.PI * 220 * t) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 3 * t)));
        }
        return x;
    }

    private static List<SpeechChunk> Segment(float[] audio, SpeechDetector vad)
    {
        var seg = new KitSegmenter(LiveSource.Mic, vad);
        var chunks = new List<SpeechChunk>();
        for (var i = 0; i < audio.Length; i += 1600) chunks.AddRange(seg.Push(audio.AsSpan(i, Math.Min(1600, audio.Length - i))));
        if (seg.Flush() is { } last) chunks.Add(last);
        return chunks;
    }

    [Fact]
    public void Segmenter_MicSpeechAt45To52_IsCut_NeverLost()
    {
        // eval regression: mic speech at 45.5-52.0 s of the field recording produced no live line
        var chunks = Segment(Recording(70, (45.5, 52.0)), FakeSilero);
        Assert.NotEmpty(chunks);
        Assert.True(chunks.Min(c => c.Start.TotalSeconds) <= 45.6, string.Join(", ", chunks.Select(c => $"{c.Start.TotalSeconds:0.0}-{c.End.TotalSeconds:0.0}")));
        Assert.True(chunks.Max(c => c.End.TotalSeconds) >= 51.9, string.Join(", ", chunks.Select(c => $"{c.Start.TotalSeconds:0.0}-{c.End.TotalSeconds:0.0}")));
        Assert.All(chunks, c => Assert.True(c.End.TotalSeconds > 44 && c.Start.TotalSeconds < 53));
    }

    [Fact]
    public void Segmenter_CutsAtPauses_SplitsLongSpeechByEightSeconds_AndSilenceNeverReachesWhisper()
    {
        Assert.Empty(Segment(Recording(30), FakeSilero));
        var chunks = Segment(Recording(40, (2, 5), (5.8, 9), (12, 30)), FakeSilero);
        Assert.True(chunks.Count >= 4, $"{chunks.Count} chunks");
        Assert.All(chunks, c => Assert.True(c.Seconds <= 8.5, $"{c.Seconds:0.0} s"));
        Assert.Contains(chunks, c => c.Start.TotalSeconds < 2.1);
        // the detector failing (no Silero yet, its process crashed): loudness takes over, speech still comes through
        var seg = new KitSegmenter(LiveSource.Speaker, (_, _) => null);
        var audio = Recording(20, (3, 9));
        var got = new List<SpeechChunk>();
        for (var i = 0; i < audio.Length; i += 1600) got.AddRange(seg.Push(audio.AsSpan(i, 1600)));
        if (seg.Flush() is { } last) got.Add(last);
        Assert.True(seg.Fallbacks > 0);
        Assert.NotEmpty(got);
    }

    // ================= audio =================

    [Fact]
    public void SoftLimiter_LeavesNormalLevelsAlone_AndNeverClips()
    {
        Assert.Equal(0.5f, SoftLimiter.Limit(0.5f));
        Assert.Equal(-0.7f, SoftLimiter.Limit(-0.7f));
        foreach (var x in new[] { 0.9f, 1.0f, 1.6f, 1.8f, 4f })
        {
            Assert.True(SoftLimiter.Limit(x) < SoftLimiter.Ceiling + 1e-6f, $"{x}");
            Assert.True(SoftLimiter.Limit(-x) > -SoftLimiter.Ceiling - 1e-6f, $"{-x}");
        }
        Assert.True(SoftLimiter.Limit(1.6f) > SoftLimiter.Limit(1.0f)); // smooth, still rising
        Assert.Equal(0f, SoftLimiter.Limit(float.NaN));
        // mic and system audio both near full scale: the mixed.wav sum stays under -1 dBFS
        Assert.True(Math.Abs(SoftLimiter.Limit(0.95f + 0.9f)) <= SoftLimiter.Ceiling);
    }

    // ================= log =================

    [Fact]
    public void Log_ThatCannotBeWritten_MovesToAFallbackFile_AndSaysWhy()
    {
        Log.ResetFallbacks();
        Log.Info("first line");
        Assert.Null(Log.Failure);
        using (new FileStream(Log.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Log.Info("written while recbox.log is locked");
        }
        Assert.NotNull(Log.Failure);
        Assert.Contains("recbox.log could not be written", Log.Failure);
        Assert.NotEqual(Log.FilePath, Log.CurrentFilePath);
        Assert.EndsWith($".pid-{Environment.ProcessId}.log", Log.CurrentFilePath);
        var fallback = File.ReadAllText(Log.CurrentFilePath);
        Assert.Contains("written while recbox.log is locked", fallback);
        Assert.Contains("could not be written", File.ReadAllText(Log.WorkerFilePath)); // surfaced in the other log too
        Log.Info("after the lock");
        Assert.Contains("after the lock", File.ReadAllText(Log.CurrentFilePath)); // never silent
        Log.ResetFallbacks();
        Log.Info("back to the real file");
        Assert.Contains("back to the real file", File.ReadAllText(Log.FilePath));
    }
}

/// <summary>
/// Owner rule: nothing RecBox runs for screenshots or tests may put a window on the owner's desktop. These read the
/// sources (the test project never starts RecBox.exe): the render flag is handled before any app startup, the render
/// uses a message-only window and never shows the main window, and RecBox.exe is only ever launched through
/// tools/run-hidden.ps1 (a separate, invisible desktop).
/// </summary>
public sealed class NoVisibleWindowTests
{
    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "RecBox.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("RecBox.sln not found above the test folder");
    }

    [Fact]
    public void RenderFlag_IsHandledBeforeAnyAppStartup()
    {
        var program = File.ReadAllText(Path.Combine(Repo(), "src", "RecBox", "Program.cs"));
        var flag = program.IndexOf("UiRender.IsCall(args)", StringComparison.Ordinal);
        Assert.True(flag > 0);
        Assert.True(flag < program.IndexOf("VelopackApp.Build()", StringComparison.Ordinal));
        Assert.True(flag < program.IndexOf("app.Run()", StringComparison.Ordinal));
        Assert.True(flag < program.IndexOf("MicBooster.RecoverAtStartup", StringComparison.Ordinal));
        // App.xaml's StartupUri only opens the window from Application.Run, which the render never calls
        var render = File.ReadAllText(Path.Combine(Repo(), "src", "RecBox", "UiRender.cs"));
        Assert.DoesNotContain("app.Run(", render);
        Assert.DoesNotContain("Current.Run(", render);
    }

    [Fact]
    public void Render_NeverShowsAWindow_AndChecksThatNoneIsVisible()
    {
        var render = File.ReadAllText(Path.Combine(Repo(), "src", "RecBox", "UiRender.cs"));
        Assert.Contains("ParentWindow = new IntPtr(-3)", render); // HWND_MESSAGE: never on screen, taskbar or Alt-Tab
        Assert.Contains("WindowStyle = 0", render); // no WS_VISIBLE
        Assert.Contains("NoHandle(window", render);
        Assert.Contains("VisibleTopLevelWindows()", render);
        // WPF runs OnStartup and opens StartupUri on the first pump, even without Application.Run (that is how
        // RecBox windows and "RecBox" message boxes reached the owner's desktop): no StartupUri, and headless
        // OnStartup returns before the main window and the message box
        var headless = render.IndexOf("App.Headless = true", StringComparison.Ordinal);
        Assert.True(headless > 0 && headless < render.IndexOf("new App()", StringComparison.Ordinal));
        Assert.DoesNotContain("StartupUri", File.ReadAllText(Path.Combine(Repo(), "src", "RecBox", "App.xaml")));
        var app = File.ReadAllText(Path.Combine(Repo(), "src", "RecBox", "App.xaml.cs"));
        var early = app.IndexOf("if (Headless)", StringComparison.Ordinal);
        Assert.True(early > 0 && early < app.IndexOf("MessageBox.Show", StringComparison.Ordinal) && early < app.IndexOf("MainWindow.Show()", StringComparison.Ordinal));
        foreach (var call in new[] { ".ShowDialog(", "EnsureHandle", "Activate()" })
            Assert.DoesNotContain(call, render);
        // the one Show(): only on the invisible "recbox-test-..." desktop that tools/run-hidden.ps1 creates
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(render, @"\.Show\(\)"));
        Assert.Contains("if (HiddenDesktop.IsCurrentThreadOnHiddenDesktop()) return await ShootShownAsync(window, path);", render);
        Assert.Contains("n.StartsWith(\"recbox-test-\"", File.ReadAllText(Path.Combine(Repo(), "src", "RecBox", "HiddenDesktop.cs")));
        Assert.Contains("recbox-test-", File.ReadAllText(Path.Combine(Repo(), "tools", "run-hidden.ps1")));
    }

    [Fact]
    public void RecBoxExe_IsOnlyEverLaunchedOnTheHiddenDesktop()
    {
        var repo = Repo();
        Assert.True(File.Exists(Path.Combine(repo, "tools", "run-hidden.ps1")));
        foreach (var f in Directory.GetFiles(Path.Combine(repo, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.EndsWith(nameof(NoVisibleWindowTests) + ".cs")) continue;
            var text = File.ReadAllText(f);
            if (f.EndsWith("SpeechQualityTests.cs")) text = text[..text.IndexOf("public sealed class NoVisibleWindowTests", StringComparison.Ordinal)];
            Assert.False(text.Contains("RecBox.exe", StringComparison.OrdinalIgnoreCase) && !text.Contains("run-hidden.ps1"), $"{f} starts RecBox.exe without run-hidden.ps1");
        }
    }
}
