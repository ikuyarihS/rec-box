using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RecBox.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RecBox;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        _vm = new MainViewModel();
        DataContext = _vm;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplicationThemeManager.ApplySystemTheme();
            SystemThemeWatcher.Watch(this);
        };
        Activated += (_, _) =>
        {
            if (!_vm.IsRecording && !_vm.IsBusy) _vm.RefreshDevices();
        };
    }

    /// <summary>
    /// Space starts or stops recording from anywhere on the record view, except where Space already
    /// means something (typing, opening a list, flipping a switch).
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space && !_vm.SettingsOpen && !e.IsRepeat &&
            e.OriginalSource is not (TextBoxBase or ComboBox or ComboBoxItem or ToggleButton))
        {
            e.Handled = true;
            Core.Log.Info($"space pressed on {e.OriginalSource.GetType().Name}");
            _vm.ToggleRecordCommand.Execute(null);
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Closing while recording, saving or transcribing: cancel this close, let the one running save finish
    /// (never start a second one), then close for real.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_readyToClose && _vm.HasWorkToFinish)
        {
            e.Cancel = true;
            if (!_closePending)
            {
                _closePending = true;
                _ = CloseWhenSavedAsync();
            }
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// The window closed although OnClosing cancelled (Application.Shutdown, which WPF runs when Windows
    /// logs off or restarts, ignores Cancel): the process ends next, so save now, synchronously.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        if (!_readyToClose && _vm.HasWorkToFinish) _vm.FinishBeforeExitNow();
        base.OnClosed(e);
    }

    private bool _readyToClose;
    private bool _closePending;

    private async Task CloseWhenSavedAsync()
    {
        try
        {
            await _vm.PrepareToExitAsync();
        }
        catch (Exception ex)
        {
            Core.Log.Error("finishing work before closing failed", ex);
        }
        _readyToClose = true;
        // posted, so Close never runs inside the Closing handler that started this
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
