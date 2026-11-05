using System.Reflection;
using RecBox.Core;
using RecBox.Core.Audio;
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
            return TranscriptionWorker.Run(args);
        }

        // must run first: handles install / update / uninstall hooks and exits early for them
        VelopackApp.Build().Run();

        SystemInfo.AppVersion = AppVersion;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.WriteCrash($"AppDomain.UnhandledException (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
            if (e.IsTerminating) MicBooster.RestoreSharedIfUsed();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => MicBooster.RestoreSharedIfUsed();
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        Log.Info($"RecBox {AppVersion} starting (pid {Environment.ProcessId})");
        foreach (var line in SystemInfo.Describe().Split(Environment.NewLine)) Log.Info($"system: {line}");

        // only here, never in the worker above: a worker runs during a recording and must not undo its boost
        MicBooster.RecoverAtStartup(AppPaths.Home);

        var app = new App();
        app.InitializeComponent();
        app.Run();
        Log.Info("RecBox exited");
        return 0;
    }

    public static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Core.Audio.SessionFinalizer.AppVersion;
}
