using System.Windows;
using RecBox.Core;

namespace RecBox;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled UI error", args.Exception);
            MessageBox.Show(args.Exception.Message, "RecBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
