using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RecBox.Core;
using RecBox.Core.Coaching;
using Xunit;

namespace RecBox.Tests;

internal static class EnglishForTests
{
    /// <summary>Other tests assert English texts: the shared instance stays English whatever the PC's language.</summary>
    [ModuleInitializer]
    internal static void Init() => Loc.Instance.Set("en");
}

/// <summary>The interface in English and Vietnamese. Switch tests use their own <see cref="Loc"/>, never the shared one.</summary>
public sealed class LocalizationTests
{
    private const char EnDash = (char)0x2013;
    private const char EmDash = (char)0x2014;

    private static readonly Regex Placeholder = new(@"\{(\d+)[^}]*\}", RegexOptions.Compiled);

    private static string Repo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "RecBox.sln"))) return d.FullName;
        throw new InvalidOperationException("repo not found");
    }

    private static IEnumerable<string> Sources(string ext) =>
        Directory.EnumerateFiles(Path.Combine(Repo(), "src"), "*" + ext, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void EveryKey_HasBothLanguages_WithTheSamePlaceholders()
    {
        Assert.True(Strings.Table.Count > 200);
        foreach (var (key, (en, vi)) in Strings.Table)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), key + " (en)");
            Assert.False(string.IsNullOrWhiteSpace(vi), key + " (vi)");
            var pe = Placeholder.Matches(en).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            var pv = Placeholder.Matches(vi).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            Assert.True(pe.SequenceEqual(pv), $"{key}: placeholders differ");
            Assert.DoesNotContain(EnDash, en + vi);
            Assert.DoesNotContain(EmDash, en + vi);
            Assert.Equal(en.Trim(), en);
        }
    }

    [Fact]
    public void Vietnamese_HasDiacritics_AndIsNotACopyOfEnglish()
    {
        var vi = string.Concat(Strings.Table.Values.Select(v => v.Vi));
        foreach (var c in "ăâđêôơưàáạảãếềệốồộớờợ") Assert.Contains(c, vi);
        var same = Strings.Table.Count(kv => kv.Value.En == kv.Value.Vi);
        Assert.True(same < Strings.Table.Count / 10, $"{same} texts are the same in both languages");
        Assert.Equal("Cài đặt", Loc.Get("vi", "x_settings"));
        Assert.Equal("Settings", Loc.Get("en", "x_settings"));
    }

    [Fact]
    public void EveryKeyTheXamlUses_Exists_AndNoLiteralTextIsLeft()
    {
        var used = 0;
        var allowed = new HashSet<string> { "RecBox", "http://localhost:11434/v1", "llama3.1" };
        foreach (var f in Sources(".xaml"))
        {
            var xaml = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(xaml, @"\{l:T\s+(?:Key=)?([A-Za-z0-9_]+)\s*\}"))
            {
                used++;
                Assert.True(Loc.Has(m.Groups[1].Value), $"{Path.GetFileName(f)}: missing key {m.Groups[1].Value}");
            }
            foreach (Match m in Regex.Matches(xaml, @"\s(Text|Content|Header|Title|ToolTip|PlaceholderText|Message|Description)=""([^""{]*[A-Za-z][^""]*)"""))
                Assert.True(allowed.Contains(m.Groups[2].Value), $"{Path.GetFileName(f)}: literal {m.Groups[1].Value}=\"{m.Groups[2].Value}\"");
        }
        Assert.True(used > 100, $"only {used} texts in the XAML");
    }

    [Fact]
    public void EveryKeyTheCodeUses_Exists()
    {
        var used = 0;
        foreach (var f in Sources(".cs"))
        {
            var code = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(code, @"Loc\.(?:T|F|Has)\(\s*""([A-Za-z0-9_]+)""\s*[,)]|""(model_tier_[a-z]+)"""))
            {
                var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (m.Value.StartsWith("Loc.Has", StringComparison.Ordinal)) continue;
                used++;
                Assert.True(Loc.Has(key), $"{Path.GetFileName(f)}: missing key {key}");
            }
        }
        Assert.True(used > 100, $"only {used} texts in the code");

        // the keys made from a prefix
        foreach (var s in CoachStyles.All) Assert.True(Loc.Has("style_" + s.Key), "style_" + s.Key);
        foreach (var l in CoachLanguages.All.Where(l => l.Code != "same")) Assert.True(Loc.Has("lang_" + l.Code), "lang_" + l.Code);
        foreach (var t in TranslateTargets.All.Where(t => t.Length > 0)) Assert.True(Loc.Has("lang_" + t), "lang_" + t);
        foreach (var k in new[] { CoachProviderKind.ClaudeCli, CoachProviderKind.CodexCli, CoachProviderKind.GeminiCli, CoachProviderKind.OpenAiCompatible })
            Assert.True(Loc.Has("ai_option_" + k), "ai_option_" + k);
    }

    [Fact]
    public void Default_FollowsTheWindowsDisplayLanguage()
    {
        Assert.Equal("vi", Loc.Default(new CultureInfo("vi-VN")));
        Assert.Equal("vi", Loc.Default(new CultureInfo("vi")));
        Assert.Equal("en", Loc.Default(new CultureInfo("en-US")));
        Assert.Equal("en", Loc.Default(new CultureInfo("ja-JP")));
        Assert.Equal("en", Loc.Default(CultureInfo.InvariantCulture));
        Assert.Equal("vi", Loc.Resolve("vi"));
        Assert.Equal("en", Loc.Resolve("en"));
        Assert.Equal(Loc.Default(Loc.OsCulture), Loc.Resolve(null));
        Assert.Equal(Loc.Default(Loc.OsCulture), Loc.Resolve("fr"));
    }

    [Fact]
    public void Switch_AppliesLive_EveryBoundTextIsToldAtOnce()
    {
        var loc = new Loc();
        loc.Set("en");
        var props = new List<string?>();
        var changed = 0;
        ((INotifyPropertyChanged)loc).PropertyChanged += (_, e) => props.Add(e.PropertyName);
        loc.Changed += () => changed++;
        Assert.Equal("Settings", loc["x_settings"]);

        loc.Set("vi");
        Assert.Equal("vi", loc.Language);
        Assert.Equal("Cài đặt", loc["x_settings"]);
        Assert.Contains("Item[]", props); // what {l:T} bindings listen to
        Assert.Equal(1, changed); // what the view model's texts listen to

        props.Clear();
        loc.Set("vi"); // unchanged: nothing redrawn
        Assert.Empty(props);
        Assert.Equal(1, changed);

        loc.Set("en");
        Assert.Equal("Settings", loc["x_settings"]);
        Assert.Equal(2, changed);
        Assert.Equal("no_such_key", loc["no_such_key"]);
        Assert.Equal("en", Loc.Instance.Language); // the shared one never moved
    }

    [Fact]
    public void Format_UsesTheTextOfTheLanguage()
    {
        Assert.Equal("Coach started (Claude Code).", Loc.F("coach_started", "Claude Code"));
        Assert.Contains("Claude Code", string.Format(CultureInfo.InvariantCulture, Loc.Get("vi", "coach_started"), "Claude Code"));
    }

    [Theory]
    // texts the live panel, Settings, the main screen and the Coach tab show, made in Core: through Loc, never English literals
    [InlineData("src/RecBox.Core/Transcription/LiveTranscription.cs", @"Note\([^;]*\$""\((skipped|could not|\{left)")]
    [InlineData("src/RecBox.Core/Transcription/LiveTranscription.cs", @"ReportGpu\(\$""The graphics card")]
    [InlineData("src/RecBox.Core/Transcription/LiveTranscription.cs", @"LiveWorkerCrashedException\(\$?""The live speech engine")]
    [InlineData("src/RecBox.Core/Transcription/TranscriptionService.cs", @"\$""Auto picked|RuntimeNote = \$""The graphics card|info\.Error = \$?""(The|Transcription)")]
    [InlineData("src/RecBox.Core/Transcription/TranscriptionService.cs", @"return \(false, \$?""(the|no|\{gpu)")]
    [InlineData("src/RecBox.Core/Transcription/TranscriptionWorker.cs", @"\$""(Not enough free memory|The speech engine|Transcription failed on|Live text failed)")]
    [InlineData("src/RecBox.Core/Coaching/Coach.cs", @"CoachProviderException\(\$""(Claude Code|Codex|Gemini CLI) answered nothing")]
    [InlineData("src/RecBox.Core/Coaching/MeetingKit.cs", @"Note\?\.Invoke\(\$""Slides:")]
    [InlineData("src/RecBox.Core/Audio/RecordingSession.cs", @"InvalidOperationException\(""Turn on")]
    public void ScreenTextsMadeInCore_GoThroughLoc(string file, string english)
    {
        var code = File.ReadAllText(Path.Combine(Repo(), file));
        var hit = Regex.Match(code, english);
        Assert.False(hit.Success, $"{file}: English text on screen: {hit.Value}");
        Assert.Equal("Bộ nhận dạng giọng nói trực tiếp đã dừng", Loc.Get("vi", "lte_stopped"));
    }

    [Fact]
    public void Repo_HasNoLongDashes()
    {
        var files = Sources(".cs").Concat(Sources(".xaml")).Append(Path.Combine(Repo(), "README.md"))
            .Concat(Directory.EnumerateFiles(Path.Combine(Repo(), "tests"), "*.cs", SearchOption.AllDirectories));
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.False(text.Contains(EnDash) || text.Contains(EmDash), f);
        }
    }

    [Fact]
    public void ModelPicker_FollowsInterfaceLanguage()
    {
        try
        {
            Loc.Instance.Set("vi");
            foreach (var m in RecBox.Core.Transcription.WhisperModels.Choices)
            {
                Assert.True(Loc.Has($"model_{m.Key}_detail"), m.Key);
                Assert.NotEqual(m.Detail, m.Info);
            }
            Assert.Equal("Tự động (khuyên dùng)", RecBox.Core.Transcription.WhisperModels.Auto.Label);
            Loc.Instance.Set("en");
            Assert.Equal("Auto (recommended)", RecBox.Core.Transcription.WhisperModels.Auto.Label);
            Assert.Equal("Large v3 Turbo", RecBox.Core.Transcription.WhisperModels.Find("large-v3-turbo")!.Label);
        }
        finally
        {
            Loc.Instance.Set("en");
        }
    }
}
