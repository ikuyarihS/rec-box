using System.Windows;
using RecBox.Core;

namespace RecBox;

public partial class App : Application
{
    /// <summary>
    /// Set by the off-screen render (--render-ui) before the App exists. WPF runs OnStartup and opens StartupUri as
    /// soon as the dispatcher pumps, even without Application.Run: headless, no window opens and no message box shows
    /// (both appeared on the owner's desktop before this).
    /// </summary>
    internal static bool Headless { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (Headless)
        {
            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error("render-ui: unhandled exception", args.Exception);
                Console.Error.WriteLine($"render-ui: unhandled exception: {args.Exception}");
                args.Handled = true;
            };
            base.OnStartup(e);
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            var crash = Log.WriteCrash("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                $"{args.Exception.Message}\n\n{Loc.F("crash_box", crash ?? AppPaths.LogsDir)}",
                "RecBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
        // the window opens here, not through StartupUri: WPF would open a StartupUri even for the headless render
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
