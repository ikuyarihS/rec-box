using System.Reflection;
using RecBox.Core;
using RecBox.Core.Audio;
using RecBox.Core.Coaching;
using RecBox.Core.Transcription;
using Velopack;

namespace RecBox;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // the transcription worker: same exe, no window, no Velopack hooks; a native crash ends only this process
        if (TranscriptionWorker.IsWorkerCall(args))
        {
            SystemInfo.AppVersion = AppVersion;
            Loc.FollowParent(); // its error texts reach the screen
            return TranscriptionWorker.Run(args);
        }
        // the live text worker: same isolation, one process for the whole recording
        if (TranscriptionWorker.IsLiveWorkerCall(args))
        {
            SystemInfo.AppVersion = AppVersion;
            Loc.FollowParent();
            return TranscriptionWorker.RunLive(args);
        }
        // screenshots of the main window drawn off-screen (never shown), with settings in the given folder
        if (UiRender.IsCall(args))
        {
            SystemInfo.AppVersion = AppVersion;
            return UiRender.Run(args);
        }
        // headless proof run: a saved recording through live text and the transcript, no window
        if (Replay.IsCall(args))
        {
            SystemInfo.AppVersion = AppVersion;
            return Replay.Run(args);
        }
        // headless tools: the coach over a saved recording's live text, WAV header repair
        if (Tools.IsCall(args))
        {
            SystemInfo.AppVersion = AppVersion;
            return Tools.Run(args);
        }
        // Silero voice detection for live text: same isolation, shared by both sources
        if (TranscriptionWorker.IsVadWorkerCall(args))
        {
            SystemInfo.AppVersion = AppVersion;
            Loc.FollowParent();
            return TranscriptionWorker.RunVad(args);
        }

        // must run first: handles install / update / uninstall hooks and exits early for them
        VelopackApp.Build().Run();

        SystemInfo.AppVersion = AppVersion;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.WriteCrash($"AppDomain.UnhandledException (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
            if (e.IsTerminating)
            {
                MicBooster.RestoreSharedIfUsed();
                EndChildren();
            }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            MicBooster.RestoreSharedIfUsed();
            EndChildren();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        Log.Info($"RecBox {AppVersion} starting (pid {Environment.ProcessId})");
        foreach (var line in SystemInfo.Describe().Split(Environment.NewLine)) Log.Info($"system: {line}");

        // only here, never in the worker above: a worker runs during a recording and must not undo its boost
        MicBooster.RecoverAtStartup(AppPaths.Home);
        // a RecBox killed mid-call left its coach prompt file behind; 1.3.0 previews kept API keys that nothing uses now
        CliRunner.SweepStale();
        CoachProviders.ForgetOldKeys();

        var app = new App();
        app.InitializeComponent();
        app.Run();
        EndChildren();
        Log.Info("RecBox exited");
        return 0;
    }

    /// <summary>At exit: AI tools still running are killed (their Job object kills them too), temp files and the tray icon go.</summary>
    private static void EndChildren()
    {
        try { CliRunner.KillAll(); } catch (Exception ex) { Log.Warn($"exit: AI tools not ended: {ex.Message}"); }
        try { CoachToast.RemoveAll(); } catch (Exception ex) { Log.Warn($"exit: tray icon not removed: {ex.Message}"); }
    }

    public static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Core.Audio.SessionFinalizer.AppVersion;
}
