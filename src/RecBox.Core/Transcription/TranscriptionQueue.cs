namespace RecBox.Core.Transcription;

/// <summary>
/// The background transcripts of saved sessions. Enqueue returns at once, so Stop is done as soon as the
/// save is; the jobs run one at a time in the order they came (one Whisper model in memory at a time).
/// Every queued or running session is tracked until its job ends, so closing can mark all of them.
/// A job never throws out of the queue: a failure is logged and written to that session's session.json.
/// </summary>
public sealed class TranscriptionQueue
{
    private readonly object _gate = new();
    private readonly List<string> _pending = new();
    private Task _tail = Task.CompletedTask;
    private bool _closed;

    /// <summary>Sessions queued or transcribing now, oldest first.</summary>
    public IReadOnlyList<string> Pending
    {
        get { lock (_gate) return _pending.ToArray(); }
    }

    public int Count
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>
    /// Queues the transcript of <paramref name="dir"/> behind the ones already queued and returns without
    /// waiting for it. The returned Task ends when this job has ended (tests and logs only; it never faults).
    /// The job starts on the caller's context, so a job queued from the UI thread runs its UI updates there.
    /// </summary>
    public Task Enqueue(string dir, Func<Task> job)
    {
        Task previous;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed)
            {
                Log.Info($"transcript of {dir} not queued: RecBox is closing");
                return Task.CompletedTask;
            }
            _pending.Add(dir);
            previous = _tail;
            _tail = done.Task;
        }
        return RunAfterAsync(previous, dir, job, done);
    }

    private async Task RunAfterAsync(Task previous, string dir, Func<Task> job, TaskCompletionSource done)
    {
        try
        {
            // never inline the job into Enqueue: the caller (Stop) returns first
            await Task.Yield();
            await previous;
            bool closed;
            lock (_gate) closed = _closed;
            if (closed)
            {
                Log.Info($"transcript of {dir} not started: RecBox is closing");
                return;
            }
            Log.Info($"transcript of {dir}: job started");
            await job();
            Log.Info($"transcript of {dir}: job ended");
        }
        catch (Exception ex)
        {
            Log.Error($"transcript of {dir} failed", ex);
            TranscriptionService.MarkFailed(dir, $"Transcription failed: {ex.Message}");
        }
        finally
        {
            lock (_gate) _pending.Remove(dir);
            done.TrySetResult();
        }
    }

    /// <summary>
    /// RecBox is closing: no queued job starts any more. Returns every session still queued or transcribing,
    /// so the caller can mark each one interrupted. A job already running is left to finish if it can.
    /// </summary>
    public IReadOnlyList<string> Close()
    {
        lock (_gate)
        {
            _closed = true;
            return _pending.ToArray();
        }
    }
}
