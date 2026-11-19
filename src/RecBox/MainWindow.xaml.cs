using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RecBox.Core.Transcription;
using RecBox.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RecBox;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly LiveScrollFollow _follow = new();

    public MainWindow() : this(new MainViewModel())
    {
    }

    /// <summary>False only for the screenshots of --render-ui, which set the theme themselves.</summary>
    internal static bool FollowSystemTheme { get; set; } = true;

    /// <summary>The window for a given view model (the off-screen screenshots build one without showing it).</summary>
    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = _vm;
        InitializeComponent();
        FitToWorkArea(SystemParameters.WorkArea);
        ApplyLivePanel(grow: true);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LivePanelOpen)) ApplyLivePanel(grow: IsLoaded);
        };
        LiveSplitter.DragCompleted += (_, _) =>
        {
            if (LiveColumn.ActualWidth > 0) _vm.LivePanelWidth = LiveColumn.ActualWidth;
        };
        LiveScroll.ScrollChanged += OnLiveScrollChanged;
        JumpButton.Click += (_, _) =>
        {
            _follow.JumpToLatest();
            LiveScroll.ScrollToEnd();
            JumpButton.Visibility = Visibility.Collapsed;
        };
        Loaded += (_, _) =>
        {
            if (!FollowSystemTheme) return; // off-screen screenshots pick light or dark themselves
            ApplicationThemeManager.ApplySystemTheme();
            SystemThemeWatcher.Watch(this);
        };
        Activated += (_, _) =>
        {
            if (!_vm.IsRecording && !_vm.IsBusy) _vm.RefreshDevices();
        };
    }

    /// <summary>
    /// The live text panel is a column of this window (no window of its own, nothing extra in Alt-Tab or the
    /// taskbar). Opening it widens the window by the panel when the screen has room, closing gives that back.
    /// </summary>
    public void ApplyLivePanel(bool grow)
    {
        var open = _vm.LivePanelOpen;
        var width = Math.Clamp(_vm.LivePanelWidth, 300, 900);
        var was = LiveColumn.ActualWidth;
        LivePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        LiveSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        LiveColumn.MinWidth = open ? 300 : 0;
        LiveColumn.Width = new GridLength(open ? width : 0);
        if (!grow || WindowState != WindowState.Normal) return;
        var area = SystemParameters.WorkArea;
        if (open) Width = Math.Min(Width + width + 5, Math.Max(MinWidth, area.Width));
        else if (was > 0) Width = Math.Max(MinWidth, Width - was - 5);
    }

    private void OnLiveScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_follow.OnScrollChanged(LiveScroll.VerticalOffset, LiveScroll.ScrollableHeight, e.ExtentHeightChange)) LiveScroll.ScrollToEnd();
        JumpButton.Visibility = _follow.Following ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// A small laptop screen (1366x768 at 125 or 150%) is shorter than the default window: shrink the window
    /// to the work area (never below MinWidth/MinHeight), the page scrolls for the rest.
    /// </summary>
    public void FitToWorkArea(Rect workArea)
    {
        if (workArea.Height > 0 && Height > workArea.Height) Height = Math.Max(MinHeight, workArea.Height);
        if (workArea.Width > 0 && Width > workArea.Width) Width = Math.Max(MinWidth, workArea.Width);
        Core.Log.Info($"window {Width:0}x{Height:0} (work area {workArea.Width:0}x{workArea.Height:0})");
    }

    /// <summary>For the screenshots: scrolls the main page so the live coach row and its meeting setup are in view.</summary>
    public void ScrollToCoachSetup()
    {
        try
        {
            var y = CoachSetupLink.TransformToAncestor(RecordScroll).Transform(new Point(0, 0)).Y;
            RecordScroll.ScrollToVerticalOffset(Math.Max(0, RecordScroll.VerticalOffset + y - 90));
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Space starts or stops recording from anywhere on the record view, except where Space already
    /// means something (typing, opening a list, flipping a switch).
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space && !_vm.SettingsOpen && !e.IsRepeat &&
            e.OriginalSource is not (TextBoxBase or System.Windows.Controls.PasswordBox or ComboBox or ComboBoxItem or ToggleButton))
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
        _vm.Detach();
        // the window's handle is still valid here: the coach's tray icon goes now, AI tools still running are killed
        try { CoachToast.RemoveAll(); } catch (Exception ex) { Core.Log.Warn($"tray icon not removed: {ex.Message}"); }
        try { Core.Coaching.CliRunner.KillAll(); } catch (Exception ex) { Core.Log.Warn($"AI tools not ended: {ex.Message}"); }
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
