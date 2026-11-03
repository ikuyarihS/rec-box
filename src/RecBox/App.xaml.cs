using System.Windows;
using RecBox.Core;

namespace RecBox;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var crash = Log.WriteCrash("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                $"{args.Exception.Message}\n\nRecBox keeps running. Details were saved to {crash ?? AppPaths.LogsDir}. " +
                "Settings, Copy diagnostics makes a file you can send.",
                "RecBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
