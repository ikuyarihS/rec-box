using System.Diagnostics;
using NAudio.Wave;
using RecBox.Core.Audio;
using RecBox.Core.Session;
using RecBox.Core.Settings;
using RecBox.Core.Transcription;
using Xunit;

namespace RecBox.Tests;

/// <summary>
/// Stop ends with the save; the transcript runs in the background through TranscriptionQueue, so the
/// next recording can start and stop while an older transcript still runs, and closing marks every
/// queued or running transcript interrupted.
/// </summary>
public sealed class TranscriptionQueueTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recbox-tests", Guid.NewGuid().ToString("N"));

    public TranscriptionQueueTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task StopThenRecordAgain_WhileTheFirstTranscriptRuns()
    {
        var queue = new TranscriptionQueue();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var running = 0;
        var overlapped = false;

        async Task Job(string name, Task gate)
        {
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            lock (order) order.Add(name + ":start");
            if (name == "a") firstStarted.TrySetResult();
            await gate;
            lock (order) order.Add(name + ":end");
            Interlocked.Decrement(ref running);
        }

        // recording A: stop = save, then queue its transcript (the stop path never awaits the job)
        var a = await SaveSessionAsync("a");
        var sw = Stopwatch.StartNew();
        var jobA = queue.Enqueue(a, () => Job("a", releaseFirst.Task));
        Assert.True(sw.ElapsedMilliseconds < 1000, "Enqueue waited for the transcript");
        await firstStarted.Task.WaitAsync(Wait);
        Assert.False(jobA.IsCompleted);

        // record again while A transcribes: a second session records and saves, its transcript queues
        var b = await SaveSessionAsync("b");
        Assert.False(jobA.IsCompleted);
        var jobB = queue.Enqueue(b, () => Job("b", Task.CompletedTask));
        Assert.Equal(new[] { a, b }, queue.Pending);
        await Task.Delay(200);
        Assert.False(jobB.IsCompleted); // one at a time: B waits for A

        releaseFirst.SetResult();
        await Task.WhenAll(jobA, jobB).WaitAsync(Wait);
        Assert.False(overlapped);
        Assert.Equal(new[] { "a:start", "a:end", "b:start", "b:end" }, order);
        Assert.Equal(0, queue.Count);
        Assert.Equal(2, Directory.GetFiles(_root, "mic.wav", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task CloseWithTwoPendingTranscripts_MarksBothInterrupted_AndTheQueuedOneNeverStarts()
    {
        var queue = new TranscriptionQueue();
        var a = PendingSession("a");
        var b = PendingSession("b");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bRan = false;

        var jobA = queue.Enqueue(a, async () =>
        {
            started.TrySetResult();
            await release.Task;
        });
        var jobB = queue.Enqueue(b, () =>
        {
            bRan = true;
            return Task.CompletedTask;
        });
        await started.Task.WaitAsync(Wait);

        // what MainViewModel.MarkTranscriptsInterrupted does on close, restart and logoff
        var pending = queue.Close();
        Assert.Equal(new[] { a, b }, pending);
        foreach (var dir in pending) TranscriptionService.MarkInterrupted(dir, "closed");

        Assert.Equal("interrupted", SessionManifest.Load(a)!.Transcript.Status);
        Assert.Equal("interrupted", SessionManifest.Load(b)!.Transcript.Status);
        Assert.Equal("closed", SessionManifest.Load(b)!.Transcript.Error);

        release.SetResult();
        await Task.WhenAll(jobA, jobB).WaitAsync(Wait);
        Assert.False(bRan);
        Assert.Equal(0, queue.Count);
        // nothing is queued once closed
        await queue.Enqueue(PendingSession("c"), () => throw new InvalidOperationException("must not run"));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task AJobThatThrows_IsLoggedAndWrittenAsFailed_AndTheNextOneStillRuns()
    {
        var queue = new TranscriptionQueue();
        var a = PendingSession("a");
        var b = PendingSession("b");
        var bRan = false;

        var jobA = queue.Enqueue(a, () => throw new InvalidOperationException("engine crashed"));
        var jobB = queue.Enqueue(b, () =>
        {
            bRan = true;
            return Task.CompletedTask;
        });
        await Task.WhenAll(jobA, jobB).WaitAsync(Wait);

        Assert.True(bRan);
        var m = SessionManifest.Load(a)!;
        Assert.Equal("failed", m.Transcript.Status);
        Assert.Contains("engine crashed", m.Transcript.Error);
        Assert.Equal(0, queue.Count);
    }

    private string PendingSession(string id)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        new SessionManifest { Id = id, Transcript = new TranscriptInfo { Status = "pending" } }.Save(dir);
        return dir;
    }

    /// <summary>Records (from a part file of tone) and saves one mic session with transcripts on.</summary>
    private async Task<string> SaveSessionAsync(string id)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var settings = new AppSettings
        {
            OutputFolder = _root,
            MicEnabled = true,
            SpeakerEnabled = false,
            SaveSpeechCopy = true,
            SaveMixed = true,
            TranscribeEnabled = true,
        };
        var clock = Stopwatch.StartNew();
        var rec = new SourceRecorder(SourceKind.Mic, null, dir, clock);
        const int rate = 48000;
        const int seconds = 2;
        var file = $"{rec.KindName}.part1.wav";
        using (var w = new WaveFileWriter(Path.Combine(dir, file), WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)))
        {
            var buf = new float[rate * seconds];
            for (var i = 0; i < buf.Length; i++) buf[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 440 * i / rate));
            w.WriteSamples(buf, 0, buf.Length);
        }
        rec.Segments.Add(new SegmentInfo
        {
            Index = 1,
            Device = "test",
            DeviceId = "test",
            Reason = "start",
            StartSeconds = 0,
            DurationSeconds = seconds,
            File = file,
            SampleRate = rate,
            Channels = 1,
            Encoding = "IeeeFloat 32-bit",
        });
        var session = RecordingSession.FromParts(dir, settings, rec);
        var m = await session.StopAndSaveAsync();
        Assert.Equal("pending", m.Transcript.Status);
        return dir;
    }
}
