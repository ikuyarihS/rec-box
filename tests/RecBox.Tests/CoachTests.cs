using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using RecBox.Core;
using RecBox.Core.Coaching;
using RecBox.Core.Session;
using RecBox.Core.Transcription;
using Xunit;

namespace RecBox.Tests;

/// <summary>
/// The live coach. Every provider here is a stub: a local TCP port speaking HTTP, a fake claude.cmd, or an in-process
/// fake. No test calls a real AI service or reads a real key.
/// </summary>
public sealed class CoachTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "recbox-coach-" + Guid.NewGuid().ToString("N"));

    public CoachTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static CoachLine Line(double at, string speaker, string text, bool echo = false) => new(TimeSpan.FromSeconds(at), speaker, text, echo);

    // ---------- the free local gate ----------

    [Theory]
    [InlineData("Dạ vâng ạ")]
    [InlineData("uh huh, right")]
    [InlineData("Thank you very much.")]
    [InlineData("そうですね")]
    public void Gate_DropsFillers(string text)
    {
        var r = new CoachGate().Check(Line(5, "Others", text));
        Assert.True(r.Dropped);
        Assert.False(r.Wake);
    }

    [Theory]
    [InlineData("Cảm ơn các bạn đã theo dõi và hẹn gặp lại")]
    [InlineData("Thanks for watching, see you next time")]
    [InlineData("Привет как дела у вас сегодня")]
    public void Gate_DropsJunkAndForeignScripts(string text)
    {
        var r = new CoachGate(langs: new[] { "vi", "en" }).Check(Line(5, "Others", text));
        Assert.True(r.Dropped);
        Assert.Contains("junk", r.Reasons);
    }

    [Fact]
    public void Gate_HoldsEchoOnlyMicLines_AndBundlesThemWithTheNextWake()
    {
        var g = new CoachGate();
        var echo = g.Check(Line(10, "You", "When can you send us the quote?", echo: true));
        Assert.False(echo.Wake);
        Assert.False(echo.Dropped);
        Assert.Contains("echo", echo.Reasons);
        var q = g.Check(Line(11, "Others", "When can you send us the quote?"));
        Assert.True(q.Wake);
        Assert.Contains(q.Bundle, l => l.Echo);
    }

    [Fact]
    public void Gate_WakesOnQuestionsNumbersAndActions()
    {
        var g = new CoachGate(langs: new[] { "vi", "en" });
        Assert.Contains("q", g.Check(Line(1, "Others", "Bao giờ mình triển khai được?")).Reasons);
        Assert.Contains("num", g.Check(Line(20, "Others", "The budget is about 40 thousand for this phase")).Reasons);
        Assert.Contains("act", g.Check(Line(40, "You", "We will send the contract draft tomorrow morning")).Reasons);
    }

    [Fact]
    public void Gate_JapaneseQuestionWakes_KanaKeepTheirMarks()
    {
        var r = new CoachGate(langs: new[] { "ja" }).Check(Line(1, "Others", "これはいつまでに必要ですか"));
        Assert.True(r.Wake);
        Assert.Contains("q", r.Reasons);
    }

    [Fact]
    public void Gate_HoldsRepeatsWithinTwoMinutes_AndShortLines()
    {
        var g = new CoachGate();
        Assert.True(g.Check(Line(10, "Others", "When can you send the quote for the pilot?")).Wake);
        var dup = g.Check(Line(40, "Others", "When can you send the quote for the pilot?"));
        Assert.False(dup.Wake);
        Assert.Contains("dup", dup.Reasons);
        var shortLine = g.Check(Line(50, "You", "đúng vậy"));
        Assert.False(shortLine.Wake);
        Assert.Contains("short", shortLine.Reasons);
        var yes = g.Check(Line(55, "You", "okay okay"));
        Assert.Contains("yesno", yes.Reasons);
    }

    [Fact]
    public void Gate_HeartbeatWakesAfterSixHeldLines()
    {
        var g = new CoachGate();
        var plain = new[]
        {
            "the weather was quite lovely today", "my colleague went home rather early", "lunch at the canteen tasted fine",
            "everyone liked the new office chairs", "traffic on the bridge looked heavy", "the garden behind us needs water",
        };
        GateResult? last = null;
        for (var i = 0; i < plain.Length; i++)
        {
            last = g.Check(Line(10 + i * 5, "Others", plain[i]));
            if (i < plain.Length - 1) Assert.False(last.Wake);
        }
        Assert.True(last!.Wake);
        Assert.Contains("heartbeat", last.Reasons);
        Assert.Equal(5, last.Bundle.Count);
    }

    [Fact]
    public void Gate_RecBoxNotesNeverWake()
    {
        var r = new CoachGate().Check(new CoachLine(TimeSpan.FromSeconds(3), "RecBox", "(Microphone lost, reopened)", IsSys: true));
        Assert.True(r.Sys);
        Assert.False(r.Wake);
    }

    // ---------- coalescing, the rate limit, the engine ----------

    private sealed class ScriptedProvider : ICoachProvider
    {
        private readonly Func<int, string, Task<string>> _answer;
        public ScriptedProvider(Func<int, string, Task<string>> answer, bool local = false)
        {
            _answer = answer;
            IsLocal = local;
        }
        public string Name => "stub";
        public string Model => CoachModels.Default;
        public bool IsLocal { get; }
        public List<string> Users { get; } = new();
        public int Calls;

        public async Task<CoachReply> CompleteAsync(string system, string user, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            lock (Users) Users.Add(user);
            var text = await _answer(n, user).WaitAsync(ct);
            return new CoachReply(text, 1000, 50, 200, 0);
        }
    }

    private sealed class RecordingToast : ICoachToast
    {
        public List<string> Shown { get; } = new();
        public void Show(string title, string text)
        {
            lock (Shown) Shown.Add(text);
        }
    }

    private static CoachEngine Engine(ICoachProvider p, Func<TimeSpan> clock, ICoachToast? toast = null, int maxWakes = 300,
        TimeSpan? timeout = null, CoachSettings? settings = null) =>
        new(new CoachOptions
        {
            Provider = p,
            Setup = CoachSetup.From(settings ?? new CoachSettings()),
            Clock = clock,
            Toast = toast,
            AutoPump = false,
            MaxWakes = maxWakes,
            CallTimeout = timeout ?? TimeSpan.FromSeconds(5),
        });

    [Fact]
    public async Task Coalesce_LinesWithin2_5sGoInOneCall()
    {
        var now = TimeSpan.Zero;
        var p = new ScriptedProvider((_, _) => Task.FromResult("SKIP"));
        using var e = Engine(p, () => now);
        e.Post(Line(0, "Others", "When can you send the quote?"));
        now = TimeSpan.FromSeconds(2.4);
        e.Pump();
        Assert.Equal(0, p.Calls);
        e.Post(Line(2.4, "Others", "And what is the price per seat?"));
        now = TimeSpan.FromSeconds(2.6);
        e.Pump();
        await e.WhenIdleAsync();
        Assert.Equal(1, p.Calls);
        Assert.Contains("send the quote", p.Users[0]);
        Assert.Contains("price per seat", p.Users[0]);
    }

    [Fact]
    public async Task Coalesce_LinesFurtherApartAreSeparateCalls()
    {
        var now = TimeSpan.Zero;
        var p = new ScriptedProvider((_, _) => Task.FromResult("SKIP"));
        using var e = Engine(p, () => now);
        e.Post(Line(0, "Others", "When can you send the quote?"));
        now = TimeSpan.FromSeconds(2.6);
        e.Pump();
        await e.WhenIdleAsync();
        e.Post(Line(2.6, "Others", "And what is the price per seat?"));
        now = TimeSpan.FromSeconds(5.2);
        e.Pump();
        await e.WhenIdleAsync();
        Assert.Equal(2, p.Calls);
        Assert.DoesNotContain("price per seat", p.Users[0]);
    }

    [Fact]
    public void CueThrottle_OnePerThreeMinutes()
    {
        var t = new CueThrottle(TimeSpan.FromSeconds(180));
        Assert.True(t.TryTake(TimeSpan.FromSeconds(10)));
        Assert.False(t.TryTake(TimeSpan.FromSeconds(100)));
        Assert.False(t.TryTake(TimeSpan.FromSeconds(189)));
        Assert.True(t.TryTake(TimeSpan.FromSeconds(190)));
    }

    [Fact]
    public async Task Engine_CuesAreRateLimited_AndOnlyAllowedCuesReachTheToast()
    {
        var now = TimeSpan.Zero;
        var toast = new RecordingToast();
        var p = new ScriptedProvider((n, _) => Task.FromResult($"CUE: ask about the budget, take {n}"));
        using var e = Engine(p, () => now, toast);
        foreach (var at in new[] { 0, 60, 200 })
        {
            now = TimeSpan.FromSeconds(at);
            e.Post(Line(at, "Others", $"What budget do you have for phase {at / 10}?"));
            now += TimeSpan.FromSeconds(3);
            e.Pump();
            await e.WhenIdleAsync();
        }
        var s = e.Snapshot();
        Assert.Equal(3, s.Calls);
        Assert.Equal(2, s.Cues.Count);
        Assert.Equal(1, s.ThrottledCues);
        Assert.Equal(2, toast.Shown.Count);
    }

    [Fact]
    public async Task Engine_PostNeverWaitsOnAHungProvider_AndTheTimeoutBacksOff()
    {
        var now = TimeSpan.Zero;
        var p = new ScriptedProvider((_, _) => Task.Delay(Timeout.Infinite).ContinueWith(_ => ""));
        using var e = Engine(p, () => now, timeout: TimeSpan.FromMilliseconds(300));
        e.Post(Line(0, "Others", "When can you send the quote?"));
        now = TimeSpan.FromSeconds(3);
        e.Pump();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 300; i++) e.Post(Line(3 + i * 0.1, "Others", $"line number {i} about the budget and the plan"));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"posting took {sw.ElapsedMilliseconds} ms");
        await e.WhenIdleAsync();
        var s = e.Snapshot();
        Assert.Equal(1, s.Failures);
        Assert.Contains("Trying again in 30 s", s.Status);
        Assert.Contains("recording and live text go on", s.Status);
        var stop = Stopwatch.StartNew();
        await e.StopAsync(TimeSpan.Zero);
        Assert.True(stop.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public async Task Engine_FailedCallIsRetriedAfterTheBackoff_WithTheSameLines()
    {
        var now = TimeSpan.Zero;
        var p = new ScriptedProvider((n, _) => n == 1 ? Task.FromException<string>(new CoachProviderException("stub said 529")) : Task.FromResult("LOG: retry worked"));
        using var e = Engine(p, () => now);
        e.Post(Line(0, "Others", "When can you send the quote?"));
        now = TimeSpan.FromSeconds(3);
        e.Pump();
        await e.WhenIdleAsync();
        Assert.Equal(1, e.Snapshot().Failures);
        now = TimeSpan.FromSeconds(10);
        e.Pump();
        await e.WhenIdleAsync();
        Assert.Equal(1, p.Calls); // still backing off
        now = TimeSpan.FromSeconds(34);
        e.Pump();
        await e.WhenIdleAsync();
        Assert.Equal(2, p.Calls);
        Assert.Contains("send the quote", p.Users[1]);
        var s = e.Snapshot();
        Assert.Equal(1, s.Calls);
        Assert.Single(s.Notes);
    }

    [Fact]
    public async Task Engine_SameLineAnsweredTwice_IsWrittenOnce()
    {
        var now = TimeSpan.Zero;
        var p = new ScriptedProvider((_, _) => Task.FromResult("LOG: the price is fixed for this year"));
        using var e = Engine(p, () => now);
        for (var i = 0; i < 2; i++)
        {
            e.Post(Line(i * 10, "Others", $"Is the price fixed for year {2026 + i}?"));
            now = TimeSpan.FromSeconds(i * 10 + 3);
            e.Pump();
            await e.WhenIdleAsync();
        }
        Assert.Equal(2, p.Calls);
        Assert.Single(e.Snapshot().Notes);
    }

    [Fact]
    public async Task Engine_StopsCallingAtTheCap_AndSaysSo()
    {
        var now = TimeSpan.Zero;
        var p = new ScriptedProvider((_, _) => Task.FromResult("SKIP"));
        using var e = Engine(p, () => now, maxWakes: 2);
        for (var i = 0; i < 4; i++)
        {
            now = TimeSpan.FromSeconds(i * 10);
            e.Post(Line(i * 10, "Others", $"Question {i}: when do we start?"));
            now += TimeSpan.FromSeconds(3);
            e.Pump();
            await e.WhenIdleAsync();
        }
        Assert.Equal(2, p.Calls);
        Assert.StartsWith("Coach paused: it reached 2 calls", e.Snapshot().Status);
    }

    [Fact]
    public async Task Engine_PausedIgnoresLines()
    {
        var now = TimeSpan.Zero;
        var p = new ScriptedProvider((_, _) => Task.FromResult("SKIP"));
        using var e = Engine(p, () => now);
        e.Paused = true;
        e.Post(Line(0, "Others", "When can you send the quote?"));
        now = TimeSpan.FromSeconds(5);
        e.Pump();
        await e.WhenIdleAsync();
        Assert.Equal(0, p.Calls);
    }

    [Fact]
    public async Task Engine_AnswersTheBriefsQuestions_AndMarksNumbers()
    {
        var now = TimeSpan.Zero;
        var p = new ScriptedProvider((_, _) => Task.FromResult("- **QA:** Q1 | Friday the 21st\nNEXT: send the quote | Lan | Friday\nLOGIN is not a tag"));
        var settings = new CoachSettings { Brief = "Me: Lan\nThem: Minh\nQuestions to ask:\n- When can we ship to staging?\n- Who tests it?" };
        using var e = Engine(p, () => now, settings: settings);
        Assert.Equal(2, e.Snapshot().Questions.Count);
        e.Post(Line(0, "Others", "We can ship to staging on Friday the 21st"));
        now = TimeSpan.FromSeconds(3);
        e.Pump();
        await e.WhenIdleAsync();
        var s = e.Snapshot();
        Assert.Contains("Friday the 21st [to confirm]", s.Questions[0].Answer);
        Assert.Equal("", s.Questions[1].Answer);
        Assert.Single(s.NextSteps);
        Assert.Equal("Lan", s.NextSteps[0].Who);
        Assert.Empty(s.Notes);
        Assert.Contains("[Minh]", p.Users[0]);
    }

    [Theory]
    [InlineData("- **LOG:** price 5 million", "LOG: price 5 million")]
    [InlineData("1. CUE: ask now", "CUE: ask now")]
    [InlineData("LOGIN failed", "LOGIN failed")]
    public void Normalize_ReadsTagsInMarkdown(string raw, string want) => Assert.Equal(want, CoachEngine.Normalize(raw));

    [Fact]
    public void MarkNumbers_OnlyBeyondTheRowId()
    {
        Assert.Equal("price 5 [to confirm]", CoachEngine.MarkNumbers("LOG", "price 5"));
        Assert.Equal("Q1 | yes", CoachEngine.MarkNumbers("QA", "Q1 | yes"));
        Assert.Equal("+ | Is phase 2 paid? | ", CoachEngine.MarkNumbers("QA", "+ | Is phase 2 paid? | "));
        Assert.Equal("+ | Price? | 5 USD [to confirm]", CoachEngine.MarkNumbers("QA", "+ | Price? | 5 USD"));
        Assert.Equal("ask 5", CoachEngine.MarkNumbers("CUE", "ask 5"));
    }

    [Fact]
    public void Prompt_StaysLean_WhateverTheMeetingLength()
    {
        var state = new CoachSnapshot();
        for (var i = 0; i < 500; i++) state.Notes.Add(new CoachEntry { Time = "00:00", Text = new string('n', 100) });
        var user = CoachPrompt.User(state, new string('r', 100_000), new string('f', 100_000));
        Assert.True(user.Length < CoachPrompt.MaxRecentChars + CoachPrompt.MaxNewChars + 15 * 120 + 1000, $"{user.Length} chars");
        var sys = CoachPrompt.System(CoachSetup.From(new CoachSettings { Brief = new string('b', 100_000) }));
        Assert.True(sys.Length < CoachPrompt.MaxBriefChars + 6000);
    }

    // ---------- providers against stubs ----------

    /// <summary>A one-request-at-a-time HTTP/1.1 server on a free 127.0.0.1 port.</summary>
    private sealed class StubHttp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public string BaseUrl { get; }
        public int Status { get; set; } = 200;
        public string Response { get; set; } = "{}";
        public List<(string Method, string Path, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();

        public StubHttp()
        {
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { return; }
                using (client)
                {
                    var s = client.GetStream();
                    var head = new List<byte>();
                    var one = new byte[1];
                    while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
                    {
                        if (await s.ReadAsync(one) == 0) break;
                        head.Add(one[0]);
                    }
                    var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
                    var first = lines[0].Split(' ');
                    var headers = lines.Skip(1).Where(l => l.Contains(':'))
                        .GroupBy(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant())
                        .ToDictionary(g => g.Key, g => g.First()[(g.First().IndexOf(':') + 1)..].Trim());
                    var len = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
                    var body = new byte[len];
                    var read = 0;
                    while (read < len)
                    {
                        var n = await s.ReadAsync(body.AsMemory(read));
                        if (n == 0) break;
                        read += n;
                    }
                    lock (Requests) Requests.Add((first[0], first[1], headers, Encoding.UTF8.GetString(body)));
                    var resp = Encoding.UTF8.GetBytes(Response);
                    await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {Status} Stub\r\nContent-Type: application/json\r\nContent-Length: {resp.Length}\r\nConnection: close\r\n\r\n"));
                    await s.WriteAsync(resp);
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private const string FakeKey = "sk-ant-api03-STUBKEY-0123456789abcdef";

    // The Anthropic API provider is gone (owner: AI tools on this PC only, never a cloud API with a key). These three keep
    // what the old probes guarded: an old choice still works without a key, an error never carries a key, usage counted.

    [Fact]
    public void LegacyAnthropicChoice_FallsBackToClaudeCode_AndNoKeyIsSent()
    {
        var s = new CoachSettings { Enabled = true, Provider = CoachProviderKind.Anthropic };
        s.Migrate();
        Assert.Equal(CoachProviderKind.ClaudeCli, s.Provider);
        var legacy = new CoachSettings { Enabled = true, Provider = CoachProviderKind.Anthropic };
        try
        {
            Assert.IsType<ClaudeCliProvider>(CoachProviders.Create(legacy)); // made only, never called here
        }
        catch (CoachProviderException ex)
        {
            Assert.Contains("Claude Code", ex.Message); // not installed: says so, never asks for a key
            Assert.DoesNotContain("key", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("Claude Code (Anthropic)", CoachPrivacy.Text(legacy));
    }

    [Fact]
    public async Task ToolErrors_NeverCarryAKey()
    {
        using var server = new StubHttp { Status = 401, Response = """{"error":{"type":"authentication_error","message":"invalid key """ + FakeKey + "\"}}" };
        using var http = new HttpClient();
        var p = new OpenAiCompatibleProvider(http, server.BaseUrl + "/v1", FakeKey, "llama3.1");
        var ex = await Assert.ThrowsAsync<CoachProviderException>(() => p.CompleteAsync("s", "u", CancellationToken.None));
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("0123456789abcdef", ex.Message);
        var codex = Assert.Throws<CoachProviderException>(() => CodexCliProvider.Parse("{\"type\":\"error\",\"message\":\"401 invalid key " + FakeKey + "\"}\n", "", 1));
        Assert.DoesNotContain("0123456789abcdef", codex.Message);
        var gemini = Assert.Throws<CoachProviderException>(() => GeminiCliProvider.Parse("{\"error\":{\"message\":\"bad key " + FakeKey + "\"}}", "", 1));
        Assert.DoesNotContain("0123456789abcdef", gemini.Message);
    }

    [Fact]
    public async Task Engine_OverTheLocalServerStub_CountsTokens_NeverAPrice()
    {
        using var server = new StubHttp { Response = """{"choices":[{"message":{"role":"assistant","content":"SKIP"}}],"usage":{"prompt_tokens":1000,"completion_tokens":100}}""" };
        using var http = new HttpClient();
        var now = TimeSpan.Zero;
        using var e = Engine(new OpenAiCompatibleProvider(http, server.BaseUrl + "/v1", null, "llama3.1"), () => now);
        e.Post(Line(0, "Others", "When can you send the quote?"));
        now = TimeSpan.FromSeconds(3);
        e.Pump();
        await e.WhenIdleAsync();
        var s = e.Snapshot();
        Assert.Equal(1, s.Calls);
        Assert.Equal(1000, s.InputTokens);
        Assert.Equal(100, s.OutputTokens);
        Assert.False(s.TokensEstimated);
        Assert.DoesNotContain("$", s.UsageText);
    }

    [Theory]
    [InlineData("sk-openai-stub-key")]
    [InlineData(null)]
    public async Task OpenAiCompatible_PostsChatCompletions_BearerOnlyWithAKey(string? key)
    {
        using var server = new StubHttp
        {
            Response = """{"choices":[{"message":{"role":"assistant","content":"CUE: ask about the budget"}}],"usage":{"prompt_tokens":321,"completion_tokens":12}}""",
        };
        using var http = new HttpClient();
        var p = new OpenAiCompatibleProvider(http, server.BaseUrl + "/v1/", key, "llama3.1");
        Assert.True(p.IsLocal);
        var r = await p.CompleteAsync("sys", "usr", CancellationToken.None);
        Assert.Equal("CUE: ask about the budget", r.Text);
        Assert.Equal(321, r.InputTokens);
        Assert.Equal(12, r.OutputTokens);
        var req = Assert.Single(server.Requests);
        Assert.Equal("/v1/chat/completions", req.Path);
        if (key is null) Assert.False(req.Headers.ContainsKey("authorization"));
        else Assert.Equal($"Bearer {key}", req.Headers["authorization"]);
        using var body = JsonDocument.Parse(req.Body);
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("llama3.1", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ClaudeCli_RunsHeadlessWithTheKitsFlags_AndReadsTheJsonResult()
    {
        var cmd = Path.Combine(_dir, "claude.cmd");
        File.WriteAllText(cmd, string.Join("\r\n",
            "@echo off",
            "echo %* > \"%~dp0args.txt\"",
            "more > \"%~dp0stdin.txt\"",
            "echo {\"type\":\"result\",\"is_error\":false,\"result\":\"LOG: from the fake cli\",\"usage\":{\"input_tokens\":12,\"output_tokens\":3,\"cache_read_input_tokens\":4},\"total_cost_usd\":0.0015}",
            ""));
        var p = new ClaudeCliProvider(cmd, "claude-sonnet-5-5");
        var r = await p.CompleteAsync("system prompt", "hello from the meeting", CancellationToken.None);
        Assert.Equal("LOG: from the fake cli", r.Text);
        Assert.Equal(12, r.InputTokens);
        Assert.Equal(4, r.CacheReadTokens);
        Assert.False(r.Estimated); // the tool reported its usage
        var args = File.ReadAllText(Path.Combine(_dir, "args.txt"));
        foreach (var flag in new[] { "-p", "--output-format json", "--model claude-sonnet-5-5", "--effort low", "--system-prompt-file", "--tools", "--setting-sources", "--strict-mcp-config", "--no-session-persistence" })
            Assert.Contains(flag, args);
        Assert.Contains("hello from the meeting", File.ReadAllText(Path.Combine(_dir, "stdin.txt")));
    }

    [Fact]
    public void ClaudeCli_ParseErrors()
    {
        Assert.Throws<CoachProviderException>(() => ClaudeCliProvider.Parse("", "not logged in", 1));
        var ex = Assert.Throws<CoachProviderException>(() => ClaudeCliProvider.Parse("""{"is_error":true,"result":"Credit balance is too low"}""", "", 1));
        Assert.Contains("Credit balance", ex.Message);
        var ok = ClaudeCliProvider.Parse("noise\n{\"result\":\"SKIP\",\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}\n", "", 0);
        Assert.Equal("SKIP", ok.Text);
        Assert.Equal(5, ok.InputTokens);
    }

    [Fact]
    public async Task ClaudeCli_ReadsTheEventArrayOfClaudeCode2()
    {
        // Claude Code 2.x prints --output-format json as one line holding an array of events, the result last
        const string array = """[{"type":"system","subtype":"init","session_id":"s","tools":[]},{"type":"assistant","message":{"content":[{"type":"text","text":"SKIP"}]}},{"type":"result","subtype":"success","is_error":false,"result":"SKIP","usage":{"input_tokens":7,"output_tokens":2,"cache_read_input_tokens":3},"total_cost_usd":0.00014695}]""";
        var r = ClaudeCliProvider.Parse(array + "\n", "", 0);
        Assert.Equal("SKIP", r.Text);
        Assert.Equal(7, r.InputTokens);
        Assert.Equal(3, r.CacheReadTokens);
        Assert.Equal(2, r.OutputTokens);
        Assert.Equal("LOG: x", ClaudeCliProvider.Parse("{\"type\":\"system\"}\n{\"type\":\"result\",\"result\":\"LOG: x\"}\n", "", 0).Text);
        Assert.Equal("CUE: now", ClaudeCliProvider.Parse("{\n \"type\": \"result\",\n \"result\": \"CUE: now\"\n}\n", "", 0).Text);
        var err = Assert.Throws<CoachProviderException>(() => ClaudeCliProvider.Parse("[{\"type\":\"result\",\"is_error\":true,\"result\":\"Credit balance is too low\"}]", "", 1));
        Assert.Contains("Credit balance", err.Message);

        var cmd = Path.Combine(_dir, "claude.cmd");
        File.WriteAllText(cmd, "@echo off\r\nmore > nul\r\necho " + array + "\r\n");
        var viaProcess = await new ClaudeCliProvider(cmd, "claude-haiku-5-5").CompleteAsync("system", "hello", CancellationToken.None);
        Assert.Equal("SKIP", viaProcess.Text);
    }

    [Fact]
    public void CoachRun_PrivacyFollowsTheRunningAi_AndAFailedStartRecovers()
    {
        CoachEngine Build(ICoachProvider p) => new(new CoachOptions { Provider = p, Setup = CoachSetup.From(new CoachSettings()), Clock = () => TimeSpan.Zero, AutoPump = false });
        var local = new CoachSettings { Enabled = true, Provider = CoachProviderKind.OpenAiCompatible, Endpoint = "http://localhost:11434/v1", EndpointModel = "llama3.1" };
        var run = new CoachRun(s => s.Provider == CoachProviderKind.Anthropic ? throw new CoachProviderException("Add your Anthropic API key in the coach setup first.") : new FakeCoachProvider());
        Assert.True(run.TryStart(local, Build));
        var anth = local.Clone();
        anth.Provider = CoachProviderKind.Anthropic;
        Assert.Contains("keeps using", run.Apply(anth, Build));
        Assert.Contains("Nothing leaves", CoachPrivacy.Text(run.Described(anth)));
        run.Stop();
        Assert.Contains("Anthropic", CoachPrivacy.Text(run.Described(anth)));

        var installed = false;
        var t0 = DateTime.UtcNow;
        var waiting = new CoachRun(s => installed ? new FakeCoachProvider() : throw new CoachProviderException("Claude Code is not installed on this PC."));
        Assert.False(waiting.TryStart(local, Build, t0));
        Assert.Contains("starts by itself", waiting.Error);
        Assert.False(waiting.RetryDue(t0.AddSeconds(5)));
        installed = true;
        Assert.True(waiting.RetryDue(t0.AddSeconds(10)));
        Assert.Contains("started", waiting.Apply(local, Build));
        Assert.NotNull(waiting.Engine);
    }

    [Fact]
    public void SpeakerNames_ReachTheSavedLiveText()
    {
        var names = SpeakerNames.From("", "Me: Lan\nThem: Minh\nHotwords: Floor3D");
        var lines = new List<LiveLine> { new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7), "Others", "Flow 3D is ready") };
        LiveTranscriptStore.Save(_dir, "t", new LiveRunResult(lines, "base", "cpu", 0, 0, 1), false, names);
        var txt = File.ReadAllText(Path.Combine(_dir, LiveTranscriptStore.TextFile));
        Assert.Contains("Minh: Floor3D is ready", txt);
        Assert.Contains("Lan = microphone, Minh = system audio", txt);
        Assert.Null(SpeakerNames.From("", ""));
    }

    [Fact]
    public void Providers_MissingSetupSaysWhatToDo()
    {
        var ex = Assert.Throws<CoachProviderException>(() => CoachProviders.Create(new CoachSettings { Provider = CoachProviderKind.OpenAiCompatible, Endpoint = "not a url" }));
        Assert.Contains("endpoint", ex.Message);
        Assert.IsType<FakeCoachProvider>(CoachProviders.Create(new CoachSettings { Provider = CoachProviderKind.Fake }));
    }

    // ---------- keys and privacy ----------

    [Fact]
    public void Keys_NoneKept_TheOldKeyFileIsRemoved_AndNeverLogged()
    {
        // RecBox keeps no API keys any more: the 1.3.0 preview's coach-keys.dat goes at start, the key store is gone
        var file = Path.Combine(AppPaths.Home, "coach-keys.dat");
        Assert.StartsWith(Path.GetTempPath(), file); // the test home, never the real profile
        Directory.CreateDirectory(AppPaths.Home);
        File.WriteAllText(file, "KEYSTORETEST");
        CoachProviders.ForgetOldKeys();
        Assert.False(File.Exists(file));
        Assert.Null(typeof(CoachProviders).Assembly.GetType("RecBox.Core.Coaching.CoachKeys"));
        Assert.Null(typeof(CoachProviders).Assembly.GetType("RecBox.Core.Coaching.AnthropicProvider"));
        var log = Path.Combine(AppPaths.Home, "logs", "recbox.log");
        if (File.Exists(log))
        {
            using var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Assert.DoesNotContain("KEYSTORETEST", new StreamReader(f).ReadToEnd());
        }
    }

    [Fact]
    public void Safe_MasksKeys()
    {
        var s = CoachProviders.Safe("bad key sk-proj-abcdef1234567890XYZ and secret-123", "secret-123");
        Assert.DoesNotContain("1234567890XYZ", s);
        Assert.DoesNotContain("secret-123", s);
    }

    [Fact]
    public void Privacy_NothingLeavesOnlyWhileTrue()
    {
        Assert.Equal(CoachPrivacy.Offline, CoachPrivacy.Text(new CoachSettings { Enabled = false, Provider = CoachProviderKind.ClaudeCli }));
        var cli = CoachPrivacy.Text(new CoachSettings { Enabled = true, Provider = CoachProviderKind.ClaudeCli });
        Assert.DoesNotContain("Nothing leaves", cli);
        Assert.Contains("through Claude Code (Anthropic)", cli);
        Assert.Contains("through Codex (OpenAI)", CoachPrivacy.Text(new CoachSettings { Enabled = true, Provider = CoachProviderKind.CodexCli }));
        Assert.Contains("through Gemini CLI (Google)", CoachPrivacy.Text(new CoachSettings { Enabled = true, Provider = CoachProviderKind.GeminiCli }));
        var api = new CoachSettings { Enabled = true, Provider = CoachProviderKind.Anthropic };
        Assert.True(CoachPrivacy.SendsOut(api));
        var ollama = new CoachSettings { Enabled = true, Provider = CoachProviderKind.OpenAiCompatible, Endpoint = "http://localhost:11434/v1" };
        Assert.False(CoachPrivacy.SendsOut(ollama));
        Assert.Contains("Nothing leaves your computer", CoachPrivacy.Text(ollama));
        var cloud = new CoachSettings { Enabled = true, Provider = CoachProviderKind.OpenAiCompatible, Endpoint = "https://api.example.com/v1" };
        Assert.True(CoachPrivacy.SendsOut(cloud));
        Assert.Contains("api.example.com", CoachPrivacy.Text(cloud));
        Assert.DoesNotContain("Nothing leaves", CoachPrivacy.Text(cloud));
        Assert.True(CoachPrivacy.IsLocalEndpoint("http://127.0.0.1:1234/v1"));
        Assert.False(CoachPrivacy.IsLocalEndpoint("http://192.168.1.20:1234/v1"));
    }

    [Fact]
    public void Settings_CoachIsOffByDefault_SonnetByDefault()
    {
        var s = new RecBox.Core.Settings.AppSettings();
        Assert.False(s.Coach.Enabled);
        Assert.Equal("claude-sonnet-5-5", s.Coach.Model);
        Assert.NotNull(CoachModels.Find("claude-opus-5-5"));
        var clone = s.Clone();
        clone.Coach.Goal = "changed";
        Assert.Equal("", s.Coach.Goal);
    }

    // ---------- output files ----------

    [Fact]
    public void Store_WritesCoachFiles_AndTheSessionEntry()
    {
        var manifest = new SessionManifest { Id = "2026-11-19_10-16-03" };
        manifest.Save(_dir);
        var snap = new CoachSnapshot
        {
            Provider = "Claude Code",
            Model = CoachModels.Default,
            Goal = "Agree on a date",
            Style = "Balanced coach",
            Cues = { new CoachEntry { At = 734, Time = "12:14", Text = "Ask who tests it." } },
            Questions = { new CoachQuestion { Id = "Q1", Question = "When | ship?", Answer = "Friday", FromBrief = true, AnsweredAt = 725 } },
            NextSteps = { new CoachNextStep { At = 739, Time = "12:19", Action = "Demo", Who = "Lan", When = "today" } },
            Notes = { new CoachEntry { At = 721, Time = "12:01", Text = "Retry reviewed." } },
            Calls = 3,
            InputTokens = 3000,
            OutputTokens = 120,
            CacheReadTokens = 500,
            TokensEstimated = true,
        };
        var files = CoachStore.SaveForSession(_dir, manifest, snap, sentOut: true);
        Assert.Equal(new[] { "coach.md", "coach.json" }, files);
        var md = File.ReadAllText(Path.Combine(_dir, "coach.md"));
        Assert.Contains("## Questions", md);
        Assert.Contains("| Q1 | When / ship? | Friday |", md); // a | in a cell never breaks the table
        Assert.Contains("Ask who tests it.", md);
        Assert.DoesNotContain(((char)0x2014).ToString(), md); // no long dashes in what RecBox writes
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "coach.json"))))
        {
            Assert.Equal(734, json.RootElement.GetProperty("cues")[0].GetProperty("at").GetDouble());
            Assert.Equal("Friday", json.RootElement.GetProperty("questions")[0].GetProperty("answer").GetString());
        }
        var saved = SessionManifest.Load(_dir)!;
        Assert.NotNull(saved.Coach);
        Assert.True(saved.Coach!.SentOut);
        Assert.Equal(3, saved.Coach.Calls);
        Assert.Equal(1, saved.Coach.QuestionsAnswered);
        Assert.Equal(500, saved.Coach.CachedTokens);
        Assert.True(saved.Coach.TokensEstimated);
        Assert.DoesNotContain("$", md); // tokens only, never a price
        Assert.Contains(saved.Files, f => f.Name == "coach.md");
        Assert.Contains(saved.Files, f => f.Name == "coach.json");
        // saving again replaces, never duplicates
        CoachStore.SaveForSession(_dir, manifest, snap, sentOut: true);
        Assert.Single(SessionManifest.Load(_dir)!.Files, f => f.Name == "coach.md");
    }

    [Fact]
    public async Task Replay_LiveTranscriptThroughTheFakeCoach_WritesNotes()
    {
        var json = Path.Combine(_dir, "live-transcript.json");
        File.WriteAllText(json, """
        {"sessionId":"t","model":"small","languageSetting":"vi+en","lines":[
          {"start":1,"end":3,"source":"speaker","speaker":"Others","text":"Dạ vâng","echo":false,"emitted":4},
          {"start":5,"end":8,"source":"speaker","speaker":"Others","text":"When can you send the quote for 20 seats?","echo":false,"emitted":9},
          {"start":5,"end":8,"source":"mic","speaker":"You","text":"When can you send the quote for 20 seats?","echo":true,"emitted":9.5},
          {"start":9,"end":9,"source":"note","speaker":"RecBox","text":"(Microphone reopened)","echo":false,"emitted":9.6},
          {"start":20,"end":24,"source":"mic","speaker":"You","text":"We will send it on Friday with 3 options","echo":false,"emitted":25}
        ]}
        """);
        var lines = CoachReplay.Lines(json);
        Assert.Equal(5, lines.Count);
        Assert.True(lines[3].IsSys);
        var outDir = Path.Combine(_dir, "out");
        var said = new List<string>();
        var snap = await CoachReplay.RunAsync(lines, new CoachSettings { Enabled = true }, new FakeCoachProvider(), outDir, "t", say: said.Add);
        Assert.Equal(2, snap.Calls);
        Assert.Single(snap.Cues);
        Assert.Single(snap.Alerts);
        Assert.Contains(snap.Notes, n => n.Text.Contains("Friday"));
        Assert.DoesNotContain("$", snap.UsageText);
        Assert.True(File.Exists(Path.Combine(outDir, "coach.md")));
        Assert.Single(said);
    }

    // ---------- the kit's helpers ----------

    [Fact]
    public void Brief_NamesTermsAndQuestions()
    {
        var brief = "Me: Lan\nThem: Mr Sato, Ms Ito\nHotwords: Floor3D, TripleA\n\n## Questions to ask\n- When can we ship to staging?\n- Who owns the HSM keys?\n\nNotes: nothing";
        var (you, others) = BriefNames.Parse(brief);
        Assert.Equal("Lan", you);
        Assert.Equal("Mr Sato, Ms Ito", others);
        var qs = BriefNames.Questions(brief);
        Assert.Equal(new[] { "When can we ship to staging?", "Who owns the HSM keys?" }, qs);
        var terms = BriefNames.Terms(brief);
        Assert.Contains("Floor3D", terms);
        Assert.Contains("TripleA", terms);
        var fixedText = BriefNames.FixNames("we demo Flow 3D and Triple A today", BriefNames.NameTargets(terms));
        Assert.Contains("Floor3D", fixedText.Text);
        Assert.Contains("TripleA", fixedText.Text);
        Assert.Equal("we demo it today", BriefNames.FixNames("we demo it today", BriefNames.NameTargets(terms)).Text);
    }

    [Fact]
    public void LiveLines_GetTheBriefsNames()
    {
        var setup = CoachSetup.From(new CoachSettings { Brief = "Me: Lan\nThem: Minh" });
        Assert.Equal("Lan", setup.Label(Line(1, "You", "x")));
        Assert.Equal("Minh", setup.Label(Line(1, "Others", "x")));
        Assert.Equal("Me?echo", setup.Label(Line(1, "You", "x", echo: true)));
        Assert.Equal("Me", CoachSetup.From(new CoachSettings()).Label(Line(1, "You", "x")));
    }

    [Fact]
    public void WavRepair_FixesSizesACrashLeftAtZero()
    {
        var path = Path.Combine(_dir, "mic.wav");
        using (var w = new WaveFileWriter(path, new WaveFormat(16000, 16, 1)))
            w.Write(new byte[32000], 0, 32000);
        var dataAt = Encoding.ASCII.GetString(File.ReadAllBytes(path)).IndexOf("data", StringComparison.Ordinal);
        Assert.True(dataAt > 12);
        using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            f.Position = 4; // RIFF size
            f.Write(new byte[4]);
            f.Position = dataAt + 4; // data size
            f.Write(new byte[4]);
        }
        Assert.Equal(new[] { "mic.wav" }, WavRepair.FixAll(_dir));
        using (var r = new WaveFileReader(path))
            Assert.Equal(32000, r.Length);
        Assert.False(WavRepair.Fix(path));
    }

    private static int[] Flat(int level) => Enumerable.Repeat(level, SlideSignature.W * SlideSignature.H).ToArray();

    [Fact]
    public void Slides_NewSlidesOnly_RevisitsAndDarkFramesSkipped()
    {
        var d = new SlideDeduper();
        Assert.True(d.Accept(Flat(200)));
        Assert.False(d.Accept(Flat(202)));
        Assert.True(d.Accept(Flat(120)));
        Assert.False(d.Accept(Flat(200)));
        Assert.False(d.Accept(Flat(10)));
    }

    [Fact]
    public void Slides_SignatureAndPng()
    {
        const int w = 64, h = 36;
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 30; px[i + 1] = 60; px[i + 2] = 90; px[i + 3] = 255; }
        var sig = SlideSignature.Of(px, w, h, w * 4);
        Assert.All(sig, v => Assert.Equal(60, v));
        var png = Path.Combine(_dir, "slide.png");
        PngWriter.Write(png, px, w, h, w * 4);
        var bytes = File.ReadAllBytes(png);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }, bytes[..8]);
        Assert.Equal(w, (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19]);
        Assert.Equal(h, (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]);
    }

    [Fact]
    public void Languages_AnyOfSet_TakesTheDetectedOneInTheSet()
    {
        Assert.True(Languages.IsAnyOf("vi,ja"));
        Assert.False(Languages.IsAnyOf("vi+en"));
        Assert.Equal("ja", Languages.ChooseMixed("vi,ja", "ja", 0.3f));
        Assert.Equal("vi", Languages.ChooseMixed("vi,ja", "en", 0.95f));
        var allowed = Languages.Allowed("vi,en,ja", null);
        Assert.Contains("ja", allowed);
        Assert.Contains("en", allowed);
    }

    // ---------- AI tools on this PC: killed on Stop, pause and exit (fake CLIs only) ----------

    private static async Task<bool> Until(Func<bool> ok, int ms = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (ok()) return true;
            await Task.Delay(50);
        }
        return ok();
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A fake AI tool that reads its input, then starts a child (powershell) that hangs for two minutes.</summary>
    private string HangingCli(string name)
    {
        var cmd = Path.Combine(_dir, name + ".cmd");
        File.WriteAllText(cmd, string.Join("\r\n",
            "@echo off",
            "more > nul",
            "powershell -NoProfile -NonInteractive -Command \"$PID | Out-File -Encoding ascii '%~dp0" + name + ".pid'; Start-Sleep -Seconds 120\"",
            "echo {\"type\":\"result\",\"result\":\"SKIP\"}",
            ""));
        return cmd;
    }

    private async Task<int> ChildPid(string name)
    {
        var f = Path.Combine(_dir, name + ".pid");
        var pid = 0;
        // the fake tool may still be writing the file: a sharing error only means "not yet"
        Assert.True(await Until(() =>
        {
            try { return int.TryParse(File.ReadAllText(f).Trim(), out pid) && pid > 0; }
            catch (IOException) { return false; }
        }, 60000), "the fake tool's child never started");
        return pid;
    }

    private static ICoachProvider ToolOf(string kind, string exe) => kind switch
    {
        "codex" => new CodexCliProvider(exe, ""),
        "gemini" => new GeminiCliProvider(exe, ""),
        _ => new ClaudeCliProvider(exe, ""),
    };

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task HungTool_StopKillsItsWholeTree_AndDeletesThePromptFile(string kind)
    {
        var name = "hang-" + kind;
        var cmd = HangingCli(name);
        var parent = 0;
        string? promptFile = null;
        CliRunner.Started = p =>
        {
            if (!p.StartInfo.Arguments.Contains(_dir, StringComparison.OrdinalIgnoreCase)) return;
            parent = p.Id;
            var m = System.Text.RegularExpressions.Regex.Match(p.StartInfo.Arguments, @"[^\s""]*recbox-coach-\d+-[0-9a-f]{32}\.md");
            if (m.Success) promptFile = m.Value;
        };
        try
        {
            var now = TimeSpan.Zero;
            using var e = Engine(ToolOf(kind, cmd), () => now, timeout: TimeSpan.FromSeconds(90));
            e.Post(Line(0, "Others", "When can you send the quote?"));
            now = TimeSpan.FromSeconds(3);
            e.Pump();
            var child = await ChildPid(name);
            Assert.True(parent > 0);
            Assert.True(Alive(parent) && Alive(child));
            if (kind == "claude") Assert.True(File.Exists(promptFile), "the system prompt file exists while the call runs");
            else Assert.Null(promptFile); // Codex and Gemini get everything on stdin

            var sw = Stopwatch.StartNew();
            var snap = await e.StopAsync(TimeSpan.FromMilliseconds(200));
            Assert.True(sw.ElapsedMilliseconds < 8000, $"stop took {sw.ElapsedMilliseconds} ms");
            Assert.True(await Until(() => !Alive(parent) && !Alive(child), 5000), "the tool or its child survived Stop");
            if (promptFile is not null) Assert.False(File.Exists(promptFile));
            Assert.Contains("still running", snap.Status);
            Assert.Equal(0, snap.Failures); // ended by Stop: not a failure
            Assert.Equal(0, CliRunner.RunningCount);
        }
        finally
        {
            CliRunner.Started = null;
        }
    }

    [Fact]
    public async Task HungTool_PauseKillsIt_NoBackoff_AndALateAnswerIsNotApplied()
    {
        const string name = "hang-pause";
        var cmd = HangingCli(name);
        var parent = 0;
        CliRunner.Started = p =>
        {
            if (p.StartInfo.Arguments.Contains(_dir, StringComparison.OrdinalIgnoreCase)) parent = p.Id;
        };
        try
        {
            var now = TimeSpan.Zero;
            using var e = Engine(new ClaudeCliProvider(cmd, ""), () => now, timeout: TimeSpan.FromSeconds(90));
            e.Post(Line(0, "Others", "When can you send the quote?"));
            now = TimeSpan.FromSeconds(3);
            e.Pump();
            var child = await ChildPid(name);
            e.Paused = true;
            var idle = e.WhenIdleAsync();
            Assert.Same(idle, await Task.WhenAny(idle, Task.Delay(8000)));
            Assert.True(await Until(() => !Alive(parent) && !Alive(child), 5000), "the tool or its child survived the pause");
            var s = e.Snapshot();
            Assert.Equal(0, s.Failures);
            Assert.StartsWith("Coach off", s.Status);
            Assert.Empty(s.Cues);
        }
        finally
        {
            CliRunner.Started = null;
        }
    }

    [Fact]
    public async Task HostExit_TheJobObjectKillsTheTree_AndKillAllEndsRunningTools()
    {
        // what Windows does when RecBox ends (even killed): the job closes and every tool in it dies
        const string name = "hang-job";
        var cmd = HangingCli(name);
        using (var job = new ChildJob())
        {
            using var p = Process.Start(CliRunner.StartInfo(cmd, Array.Empty<string>()))!;
            Assert.True(job.Assign(p));
            p.StandardInput.Close(); // the child starts only now, inside the job
            var child = await ChildPid(name);
            Assert.True(Alive(p.Id) && Alive(child));
            job.Dispose();
            Assert.True(await Until(() => !Alive(p.Id) && !Alive(child), 5000), "closing the job left a process running");
        }

        // the exit path of the window and ProcessExit: KillAll ends a tool mid-call and deletes its prompt file
        const string name2 = "hang-exit";
        var cmd2 = HangingCli(name2);
        string? promptFile = null;
        var parent = 0;
        CliRunner.Started = p =>
        {
            if (!p.StartInfo.Arguments.Contains(_dir, StringComparison.OrdinalIgnoreCase)) return;
            parent = p.Id;
            promptFile = System.Text.RegularExpressions.Regex.Match(p.StartInfo.Arguments, @"[^\s""]*recbox-coach-\d+-[0-9a-f]{32}\.md").Value;
        };
        try
        {
            var call = new ClaudeCliProvider(cmd2, "").CompleteAsync("system", "hello", CancellationToken.None);
            var child2 = await ChildPid(name2);
            Assert.True(File.Exists(promptFile));
            CliRunner.KillAll();
            await Assert.ThrowsAsync<CoachProviderException>(() => call);
            Assert.True(await Until(() => !Alive(parent) && !Alive(child2), 5000), "KillAll left a process running");
            Assert.False(File.Exists(promptFile));
        }
        finally
        {
            CliRunner.Started = null;
        }
    }

    [Fact]
    public void SweepStale_RemovesFilesOfEndedRuns_KeepsThisRunsAndOthers()
    {
        string F(string n) => Path.Combine(_dir, n);
        var g = Guid.NewGuid().ToString("N");
        var mine = F($"recbox-coach-{Environment.ProcessId}-{g}.md");
        var dead = F($"recbox-coach-999999-{g}.md");
        var legacyOld = F($"recbox-coach-{Guid.NewGuid():N}.md");
        var legacyNew = F($"recbox-coach-{Guid.NewGuid():N}.md");
        var other = F("recbox-coach-notes.md");
        foreach (var f in new[] { mine, dead, legacyOld, legacyNew, other }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(legacyOld, DateTime.UtcNow.AddHours(-1));
        Assert.Equal(2, CliRunner.SweepStale(_dir));
        Assert.True(File.Exists(mine));
        Assert.False(File.Exists(dead));
        Assert.False(File.Exists(legacyOld));
        Assert.True(File.Exists(legacyNew));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task HungLocalServer_PauseCancelsTheRequest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var clients = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var c = await listener.AcceptTcpClientAsync();
                    lock (clients) clients.Add(c); // never answers
                }
            }
            catch (Exception) { }
        });
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var http = new HttpClient();
            var now = TimeSpan.Zero;
            using var e = Engine(new OpenAiCompatibleProvider(http, $"http://127.0.0.1:{port}/v1", null, "llama3.1"), () => now, timeout: TimeSpan.FromSeconds(90));
            e.Post(Line(0, "Others", "When can you send the quote?"));
            now = TimeSpan.FromSeconds(3);
            e.Pump();
            Assert.True(await Until(() => { lock (clients) return clients.Count > 0; }, 5000));
            e.Paused = true;
            var idle = e.WhenIdleAsync();
            Assert.Same(idle, await Task.WhenAny(idle, Task.Delay(5000)));
            Assert.Equal(0, e.Snapshot().Failures);
        }
        finally
        {
            listener.Stop();
            lock (clients) foreach (var c in clients) c.Dispose();
        }
    }

    // ---------- Codex CLI and Gemini CLI (not installed here: documented flags, fake CLIs) ----------

    private string FakeCli(string name, params string[] output)
    {
        var cmd = Path.Combine(_dir, name + ".cmd");
        var lines = new List<string> { "@echo off", $"echo %* > \"%~dp0{name}-args.txt\"", $"more > \"%~dp0{name}-stdin.txt\"" };
        lines.AddRange(output.Select(o => "echo " + o));
        lines.Add("");
        File.WriteAllText(cmd, string.Join("\r\n", lines));
        return cmd;
    }

    [Fact]
    public async Task Codex_ExecJsonReadOnly_PromptOnStdin_ReadsAnswerAndUsage()
    {
        Assert.Equal(new[] { "exec", "--json", "--skip-git-repo-check", "--sandbox", "read-only", "-" }, CodexCliProvider.Arguments(""));
        Assert.Contains("gpt-5-codex", CodexCliProvider.Arguments("gpt-5-codex"));
        var cmd = FakeCli("codex",
            "{\"type\":\"thread.started\",\"thread_id\":\"t1\"}",
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"i1\",\"type\":\"agent_message\",\"text\":\"CUE: ask about the budget\"}}",
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1200,\"cached_input_tokens\":200,\"output_tokens\":50}}");
        var r = await new CodexCliProvider(cmd, "gpt-5-codex").CompleteAsync("system prompt", "hello from the meeting", CancellationToken.None);
        Assert.Equal("CUE: ask about the budget", r.Text);
        Assert.Equal(1000, r.InputTokens); // cached tokens are not counted twice
        Assert.Equal(200, r.CacheReadTokens);
        Assert.Equal(50, r.OutputTokens);
        Assert.False(r.Estimated);
        var args = File.ReadAllText(Path.Combine(_dir, "codex-args.txt"));
        Assert.Contains("exec --json --skip-git-repo-check --sandbox read-only --model gpt-5-codex -", args);
        var stdin = File.ReadAllText(Path.Combine(_dir, "codex-stdin.txt"));
        Assert.Contains("system prompt", stdin);
        Assert.Contains("---", stdin);
        Assert.Contains("hello from the meeting", stdin);
    }

    [Fact]
    public void Codex_ParsesOldEventsAndErrors()
    {
        var old = CodexCliProvider.Parse(
            "{\"id\":\"0\",\"msg\":{\"type\":\"agent_message\",\"message\":\"SKIP\"}}\n" +
            "{\"id\":\"0\",\"msg\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":30,\"cached_input_tokens\":10,\"output_tokens\":2}}}}\n", "", 0);
        Assert.Equal("SKIP", old.Text);
        Assert.Equal(20, old.InputTokens);
        Assert.Equal(10, old.CacheReadTokens);
        var failed = Assert.Throws<CoachProviderException>(() => CodexCliProvider.Parse("{\"type\":\"turn.failed\",\"error\":{\"message\":\"You are not signed in\"}}\n", "", 1));
        Assert.Contains("not signed in", failed.Message);
        Assert.Throws<CoachProviderException>(() => CodexCliProvider.Parse("", "codex: command failed", 1));
    }

    [Fact]
    public async Task Gemini_PromptFlagJsonOutput_ReadsResponseAndStats()
    {
        var args = GeminiCliProvider.Arguments("gemini-2.5-flash");
        Assert.Equal("-p", args[0]);
        Assert.Contains("--output-format", args);
        Assert.Equal("gemini-2.5-flash", args[args.ToList().IndexOf("-m") + 1]);
        Assert.DoesNotContain("-m", GeminiCliProvider.Arguments(""));
        var cmd = FakeCli("gemini",
            "Loaded cached credentials.",
            "{\"response\":\"LOG: from gemini\",\"stats\":{\"models\":{\"gemini-2.5-pro\":{\"tokens\":{\"prompt\":900,\"cached\":100,\"candidates\":40}}}}}");
        var r = await new GeminiCliProvider(cmd, "").CompleteAsync("system prompt", "hello from the meeting", CancellationToken.None);
        Assert.Equal("LOG: from gemini", r.Text);
        Assert.Equal(800, r.InputTokens);
        Assert.Equal(100, r.CacheReadTokens);
        Assert.Equal(40, r.OutputTokens);
        var seen = File.ReadAllText(Path.Combine(_dir, "gemini-args.txt"));
        Assert.Contains("--output-format json", seen);
        Assert.Contains("Follow the instructions above", seen);
        Assert.Contains("hello from the meeting", File.ReadAllText(Path.Combine(_dir, "gemini-stdin.txt")));
        var err = Assert.Throws<CoachProviderException>(() => GeminiCliProvider.Parse("{\"error\":{\"message\":\"quota exceeded\"}}", "", 1));
        Assert.Contains("quota exceeded", err.Message);
    }

    [Fact]
    public async Task Tools_WithoutUsage_CountCharsOverFour_MarkedAsAnEstimate()
    {
        var cmd = FakeCli("gemini-nousage", "{\"response\":\"SKIP\"}");
        var r = await new GeminiCliProvider(cmd, "").CompleteAsync(new string('s', 400), new string('u', 400), CancellationToken.None);
        Assert.True(r.Estimated);
        Assert.Equal(200, r.InputTokens);
        var snap = new CoachSnapshot { Calls = 1, InputTokens = r.InputTokens, OutputTokens = r.OutputTokens, TokensEstimated = true };
        Assert.Contains("(estimate)", snap.UsageText);
        Assert.DoesNotContain("$", snap.UsageText);
        Assert.Matches(@"About [\d,]+ tokens an hour", UsageEstimate.Text(UsageEstimate.CoachTokensPerHour(new CoachSettings { Enabled = true })));
    }

    // ---------- Translate to ----------

    private static string Echoed(string user) =>
        string.Join("\n", user.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Replace("| ", "| VI ")));

    [Fact]
    public void TranslateChoice_DefaultIsSaved_TheMainScreenOverridesOneRecording()
    {
        var c = new TranslateChoice("vi");
        Assert.Equal("vi", c.Current);
        c.Pick("ja");
        Assert.True(c.Overridden);
        c.SetDefault("fr"); // Settings changed meanwhile: this recording keeps its pick
        Assert.Equal("ja", c.Current);
        c.RecordingEnded();
        Assert.Equal("fr", c.Current);
        c.SetDefault("de"); // not overridden: follows
        Assert.Equal("de", c.Current);
        Assert.True(new TranslateChoice("en").WhisperTranslates);
        Assert.False(new TranslateChoice("vi").WhisperTranslates);
        Assert.Equal("", new TranslateChoice("xx").Current);
        Assert.False(TranslateTargets.NeedsAi("en")); // English stays the offline Whisper translate
        Assert.True(TranslateTargets.NeedsAi("ko"));
        Assert.Equal(new[] { "", "en", "vi", "ja", "zh", "ko", "fr", "de", "es" }, TranslateTargets.All);
    }

    [Fact]
    public void Settings_TranslateToAndInterfaceLanguagePersist_OldSettingsMigrate()
    {
        var o = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var back = JsonSerializer.Deserialize<RecBox.Core.Settings.AppSettings>(
            JsonSerializer.Serialize(new RecBox.Core.Settings.AppSettings { TranslateTo = "vi", UiLanguage = "vi" }, o), o)!;
        Assert.False(back.Migrate());
        Assert.Equal("vi", back.TranslateTo);
        Assert.Equal("vi", back.UiLanguage);

        var oldMode = JsonSerializer.Deserialize<RecBox.Core.Settings.AppSettings>("{\"Mode\":\"TranslateToEnglish\"}", o)!;
        Assert.True(oldMode.Migrate());
        Assert.Equal("en", oldMode.TranslateTo);
        Assert.True(oldMode.WhisperTranslates);
        var oldLive = JsonSerializer.Deserialize<RecBox.Core.Settings.AppSettings>("{\"LiveTranslate\":true}", o)!;
        Assert.True(oldLive.Migrate());
        Assert.Equal("en", oldLive.TranslateTo);
        Assert.False(oldLive.LiveTranslate);
        var odd = JsonSerializer.Deserialize<RecBox.Core.Settings.AppSettings>("{\"UiLanguage\":\"fr\",\"Coach\":{\"Provider\":\"Anthropic\"}}", o)!;
        Assert.True(odd.Migrate());
        Assert.Null(odd.UiLanguage);
        Assert.Equal(CoachProviderKind.ClaudeCli, odd.Coach.Provider);
    }

    [Fact]
    public async Task LiveTranslator_ThroughAFakeProvider_KeepsTheOriginalAndAddsTheTranslation()
    {
        var p = new ScriptedProvider((_, u) => Task.FromResult(Echoed(u)));
        using var t = new LiveTranslator(p, "vi");
        var got = new List<string>();
        t.Translated += (k, text) => { lock (got) got.Add(text); };
        var k1 = LiveTranslator.Key(TimeSpan.FromSeconds(1), "Others", "hello there");
        t.Post(k1, "hello there", "en");
        t.Post("k2", "see you friday");
        t.Post("k3", "xin chào", "vi"); // already in the target language: left as it is
        Assert.True(await Until(() => t.Results.Count == 2, 10000));
        Assert.Equal("VI hello there", t.Get(k1));
        Assert.Null(t.Get("k3"));
        Assert.True(await Until(() => { lock (got) return got.Count == 2; }, 5000));
        Assert.True(t.Stats.InputTokens > 0);
        Assert.Contains(p.Users, u => u.Contains("1| hello there") && u.Contains("2| see you friday"));
        await t.StopAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task LiveTranslator_HungProvider_NeverBlocks_BacksOff_AndStopsAtOnce()
    {
        var p = new ScriptedProvider((_, _) => Task.Delay(Timeout.Infinite).ContinueWith(_ => ""));
        using var t = new LiveTranslator(p, "ja", callTimeout: TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 300; i++) t.Post("k" + i, $"line number {i} about the budget");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"posting took {sw.ElapsedMilliseconds} ms");
        Assert.True(t.Pending <= LiveTranslator.MaxQueue);
        Assert.True(await Until(() => t.Status.Contains("Trying again in 30 s"), 10000), t.Status);
        Assert.True(t.Stats.Failures >= 1);
        var stop = Stopwatch.StartNew();
        await t.StopAsync(TimeSpan.Zero);
        Assert.True(stop.ElapsedMilliseconds < 5000, $"stop took {stop.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task TranscriptTranslation_WritesTranscriptLangMd_ReusesLiveLines_AndRecordsIt()
    {
        File.WriteAllText(Path.Combine(_dir, "transcript.md"), "# Transcript\n\n[00:00:01] **You:** hello there\n\n[00:00:05] **Others:** see you friday\n");
        var p = new ScriptedProvider((_, u) => Task.FromResult(Echoed(u)));
        var known = new Dictionary<string, string> { ["hello there"] = "xin chào" };
        var r = await TranscriptTranslator.TranslateAsync(p, "vi", _dir, known, null, CancellationToken.None);
        Assert.Null(r.Error);
        Assert.Equal("transcript.vi.md", r.File);
        Assert.Equal(2, r.Translated);
        Assert.Equal(1, p.Calls); // the live line's translation was reused
        var md = File.ReadAllText(Path.Combine(_dir, "transcript.vi.md"));
        Assert.Contains("[00:00:01] **You:** xin chào", md);
        Assert.Contains("> hello there", md);
        Assert.Contains("**Others:** VI see you friday", md);
        var manifest = new SessionManifest { Id = "t" };
        TranscriptTranslator.Record(_dir, manifest, new TranslationInfo { To = "vi", Provider = p.Name, File = r.File, TranscriptLines = r.Translated, SentOut = true });
        var saved = SessionManifest.Load(_dir)!;
        Assert.Equal("vi", saved.Translation!.To);
        Assert.Contains(saved.Files, f => f.Name == "transcript.vi.md");
    }

    [Fact]
    public void LiveText_SavesBothTexts()
    {
        var line = new LiveLine(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7), "Others", "see you friday");
        var tr = new Dictionary<string, string> { [LiveTranslator.Key(line.Start, line.Speaker, line.Text)] = "hẹn gặp thứ Sáu" };
        LiveTranscriptStore.Save(_dir, "t", new LiveRunResult(new List<LiveLine> { line }, "base", "cpu", 0, 0, 1), false, null, tr, "vi");
        var txt = File.ReadAllText(Path.Combine(_dir, LiveTranscriptStore.TextFile));
        Assert.Contains("Others: see you friday", txt);
        Assert.Contains("    [vi] hẹn gặp thứ Sáu", txt);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, LiveTranscriptStore.JsonFile)));
        Assert.Equal("vi", json.RootElement.GetProperty("translateTo").GetString());
        var l = json.RootElement.GetProperty("lines")[0];
        Assert.Equal("see you friday", l.GetProperty("text").GetString());
        Assert.Equal("hẹn gặp thứ Sáu", l.GetProperty("translation").GetString());
    }

    [Fact]
    public void Privacy_TranslationNamesTheVendor_EnglishStaysOffline()
    {
        var off = new CoachSettings { Enabled = false, Provider = CoachProviderKind.ClaudeCli };
        Assert.Equal(CoachPrivacy.Offline, CoachPrivacy.Text(off, "en"));
        Assert.False(CoachPrivacy.SendsOut(off, "en"));
        var vi = CoachPrivacy.Text(off, "vi");
        Assert.Contains("lines to translate", vi);
        Assert.Contains("Claude Code (Anthropic)", vi);
        Assert.DoesNotContain("Nothing leaves", vi);
        Assert.True(CoachPrivacy.SendsOut(off, "vi"));
        Assert.Contains("Codex (OpenAI)", CoachPrivacy.Text(new CoachSettings { Provider = CoachProviderKind.CodexCli }, "ja"));
        var local = new CoachSettings { Provider = CoachProviderKind.OpenAiCompatible, Endpoint = "http://localhost:11434/v1" };
        Assert.Contains("Nothing leaves your computer", CoachPrivacy.Text(local, "vi"));
        Assert.Throws<CoachProviderException>(() => CoachProviders.Create(new CoachSettings { Provider = CoachProviderKind.OpenAiCompatible, Endpoint = "https://api.example.com/v1" }));
    }

    // ---------- the gate's languages, the prompt's questions, replay labels, dashes ----------

    [Fact]
    public void GateLanguages_AutoUsesTheLockedLanguagePlusEnglish_TheKitSetBefore()
    {
        Assert.Equal(new[] { "vi", "en", "ja" }, CoachLanguages.ForGate("auto", null));
        Assert.Equal(new[] { "ja", "en" }, CoachLanguages.ForGate("auto", "ja"));
        Assert.Equal(new[] { "vi", "en" }, CoachLanguages.ForGate("vi", "ja"));
        Assert.Equal(new[] { "en" }, CoachLanguages.ForGate("en", null));
        var now = TimeSpan.Zero;
        using var e = Engine(new FakeCoachProvider(), () => now);
        e.SetLanguages(CoachLanguages.ForGate("auto", "ko"));
        Assert.Equal(new[] { "ko", "en" }, e.Languages);

        var json = Path.Combine(_dir, "live-transcript.json");
        File.WriteAllText(json, """{"sessionId":"t","model":"small","languageSetting":"auto","languageDetected":"ja","lines":[]}""");
        Assert.Equal(new[] { "ja", "en" }, CoachReplay.Languages(json));
        File.WriteAllText(json, """{"sessionId":"t","model":"small","languageSetting":"auto","lines":[]}""");
        Assert.Equal(new[] { "vi", "en", "ja" }, CoachReplay.Languages(json));
    }

    [Fact]
    public void Prompt_SendsOpenAndRecentQuestions_AnswersAreCapped()
    {
        var all = Enumerable.Range(1, 50).Select(i => new CoachQuestion
        {
            Id = "Q" + i,
            Question = "question " + i,
            Answer = i <= 30 ? "answer " + i : "",
            AnsweredAt = i <= 30 ? i * 10 : null,
        }).ToList();
        var sent = CoachPrompt.PromptQuestions(all);
        Assert.Equal(CoachPrompt.MaxQuestions, sent.Count);
        Assert.All(all.Where(q => q.Answer.Length == 0), q => Assert.Contains(q, sent)); // every open one
        Assert.Contains(all[29], sent); // the newest answer
        Assert.DoesNotContain(all[0], sent); // the oldest answers make room
        Assert.Equal(sent.OrderBy(q => int.Parse(q.Id[1..])).ToList(), sent); // table order

        var longAnswer = string.Join(" / ", Enumerable.Range(1, 80).Select(i => $"00:{i:00}:00 answer part {i}"));
        var capped = CoachEngine.CapAnswer(longAnswer);
        Assert.True(capped.Length <= CoachEngine.MaxAnswerChars);
        Assert.StartsWith("... ", capped);
        Assert.EndsWith("answer part 80", capped);
        var forPrompt = CoachPrompt.PromptAnswer(longAnswer);
        Assert.True(forPrompt.Length <= CoachPrompt.MaxAnswerChars + 3);
        Assert.EndsWith("answer part 80", forPrompt);
    }

    [Fact]
    public void Replay_LabelsLinesBySource_EvenWithTheBriefsNames()
    {
        var json = Path.Combine(_dir, "live-named.json");
        File.WriteAllText(json, """
        {"sessionId":"t","model":"small","languageSetting":"vi","lines":[
          {"start":1,"end":3,"source":"mic","speaker":"Lan","text":"We ship on Friday","echo":false,"emitted":4},
          {"start":5,"end":8,"source":"speaker","speaker":"Minh","text":"Who tests the retry?","echo":false,"emitted":9},
          {"start":9,"end":9,"source":"note","speaker":"RecBox","text":"(Microphone reopened)","echo":false,"emitted":9.6},
          {"start":10,"end":11,"speaker":"Others","text":"an old file without a source","echo":false}
        ]}
        """);
        var lines = CoachReplay.Lines(json);
        Assert.Equal("You", lines[0].Speaker);
        Assert.Equal("Others", lines[1].Speaker);
        Assert.True(lines[2].IsSys);
        Assert.Equal("Others", lines[3].Speaker);
    }

    [Fact]
    public void Gate_FillersWithDashesAreStillFillers()
    {
        Assert.True(new CoachGate().Check(Line(5, "Others", "D\u1EA1 \u2013 v\u00E2ng \u2014")).Dropped);
        Assert.True(new CoachGate().Check(Line(5, "Others", "uh huh \u2014 right")).Dropped);
    }

    [Fact]
    public void Diagnostics_SettingsLoseTheMeetingAndAnyKey()
    {
        var f = Path.Combine(_dir, "settings.json");
        File.WriteAllText(f, """
        {"OutputFolder":"D:\\Rec","UpdateToken":"tok-123456","Coach":{"Enabled":true,"Goal":"secret goal","Brief":"Me: Lan","Role":"seller",
         "Instructions":"be blunt","BriefFile":"C:\\brief.md","Endpoint":"http://localhost:11434/v1","ApiKey":"sk-abc123"}}
        """);
        var clean = Diagnostics.CleanSettings(f);
        foreach (var gone in new[] { "secret goal", "Me: Lan", "seller", "be blunt", "brief.md", "sk-abc123", "tok-123456" })
            Assert.DoesNotContain(gone, clean);
        Assert.Contains("(removed)", clean);
        Assert.Contains("OutputFolder", clean);
        Assert.Contains("localhost:11434", clean);
        File.WriteAllText(f, "{ not json, Brief: secret");
        Assert.DoesNotContain("secret", Diagnostics.CleanSettings(f));
    }

    // ---------- the app's exit paths (the tests reference RecBox.Core only: these read the app's code) ----------

    private static string Repo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "RecBox.sln"))) return d.FullName;
        throw new InvalidOperationException("repo not found");
    }

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = code.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        var nextPublic = code.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        var end = new[] { next, nextPublic, code.Length }.Where(i => i > 0).Min();
        return code[start..end];
    }

    [Fact]
    public void App_SaveFinishesTheCoachEvenWhenSavingThrows_AndExitCleansUp()
    {
        var vm = File.ReadAllText(Path.Combine(Repo(), "src", "RecBox", "ViewModels", "MainViewModel.cs"));
        var save = Body(vm, "private async Task<SessionManifest?> SaveAsync(");
        var fin = save[save.LastIndexOf("finally", StringComparison.Ordinal)..];
        Assert.Contains("FinishCoach(session.Directory, manifest", fin);
        var exit = Body(vm, "public void FinishBeforeExitNow(");
        Assert.Contains("FinishCoach(", exit[exit.IndexOf("finally", StringComparison.Ordinal)..]);

        var toast = File.ReadAllText(Path.Combine(Repo(), "src", "RecBox", "CoachToast.cs"));
        Assert.Contains("Shell_NotifyIcon(NimDelete", Body(toast, "public static void RemoveAll()"));
        var program = File.ReadAllText(Path.Combine(Repo(), "src", "RecBox", "Program.cs"));
        Assert.Contains("CliRunner.SweepStale()", program);
        Assert.Contains("CliRunner.KillAll()", program);
        Assert.Contains("CoachToast.RemoveAll()", program);
        Assert.Contains("ProcessExit", program);
        var window = File.ReadAllText(Path.Combine(Repo(), "src", "RecBox", "MainWindow.xaml.cs"));
        Assert.Contains("CoachToast.RemoveAll()", window);
        Assert.Contains("CliRunner.KillAll()", window);
    }
}
