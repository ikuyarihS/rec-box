using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RecBox.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RecBox;

/// <summary>
/// `RecBox.exe --render-ui &lt;dir&gt;`: PNG screenshots of the main window (idle, recording with the live text
/// panel, transcribing, idle with the panel) in light and dark, drawn off-screen with RenderTargetBitmap. No window
/// is ever shown or activated, no device records, nothing is downloaded: the settings live in &lt;dir&gt;\home.
/// </summary>
public static class UiRender
{
    public const string Flag = "--render-ui";

    public static bool IsCall(string[] args) => args.Length >= 2 && args[0] == Flag;

    public static int Run(string[] args)
    {
        try
        {
            return Render(args);
        }
        catch (Exception ex)
        {
            // never an unhandled exception: in a GUI exe that can raise an error dialog on the owner's desktop
            Step($"failed: {ex}");
            Console.Error.WriteLine($"render-ui failed: {ex}");
            return 1;
        }
    }

    private static int Render(string[] args)
    {
        var outDir = Path.GetFullPath(args[1]);
        var home = Path.Combine(outDir, "home");
        Directory.CreateDirectory(home);
        // before anything reads AppPaths: the owner's settings, logs and recordings are never touched
        Environment.SetEnvironmentVariable("RECBOX_HOME", home);
        File.WriteAllText(Path.Combine(home, "settings.json"), JsonSerializer.Serialize(new
        {
            CheckUpdatesOnStart = false,
            TranscribeEnabled = true,
            LiveTranscribe = true,
            LivePanelOpen = false,
            LivePanelWidth = 400,
            OutputFolder = Path.Combine(home, "recordings"),
        }));

        _trace = Path.Combine(outDir, "render.log");
        File.WriteAllText(_trace, "");
        // a step that hangs ends the process with a note, instead of waiting for the caller's timeout
        var ui = Dispatcher.CurrentDispatcher;
        var watchdog = new Thread(() =>
        {
            var probed = false;
            while (true)
            {
                Thread.Sleep(5000);
                var idle = (DateTime.UtcNow - _lastStep).TotalSeconds;
                if (idle > 20 && !probed)
                {
                    probed = true;
                    // answers only if the UI thread still pumps messages (a frame that never exits), not if it is blocked
                    ui.BeginInvoke(DispatcherPriority.Send, new Action(() => Step("ui thread alive (pumping)")));
                }
                if (idle > 60)
                {
                    Step("stuck for 60 s, giving up");
                    Environment.Exit(3);
                }
            }
        }) { IsBackground = true };
        watchdog.Start();

        // software only: on the invisible desktop the graphics driver never presents, and a hardware render pass
        // blocks the dispatcher; the screenshots are drawn by RenderTargetBitmap (software) anyway
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Step($"text services off: {DisableTextServices()}");
        Step("app");
        // the App constructor queues WPF's startup (OnStartup, which opens the main window and installs a message box
        // for errors) for the first time the dispatcher pumps: headless, OnStartup opens nothing and shows no box
        App.Headless = true;
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // the steps run as dispatcher work on the thread's own message loop (Dispatcher.Run), never in nested frames:
        // a control that waits while pumping messages cannot hold the screenshots up, and the process ends explicitly
        var code = 1;
        ui.BeginInvoke(new Func<Task>(async () =>
        {
            try
            {
                code = await RenderAllAsync(app, outDir);
            }
            catch (Exception ex)
            {
                Step($"failed: {ex}");
                Console.Error.WriteLine($"render-ui failed: {ex}");
                code = 1;
            }
            Step($"exit {code}");
            Console.Out.Flush();
            Environment.Exit(code);
        }));
        Dispatcher.Run();
        return code;
    }

    private static async Task<int> RenderAllAsync(App app, string outDir)
    {
        var shots = new List<string>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        {
            Step($"theme {theme}");
            // replace the theme dictionary (changing it in place does not refresh resources); no ApplicationThemeManager:
            // it reaches for the app's main window, and nothing here may touch a window
            var merged = app.Resources.MergedDictionaries;
            for (var i = 0; i < merged.Count; i++)
                if (merged[i] is Wpf.Ui.Markup.ThemesDictionary) merged[i] = new Wpf.Ui.Markup.ThemesDictionary { Theme = theme };
            await SettleAsync();
            foreach (var (state, panel) in new[] { ("idle", false), ("recording", true), ("transcribing", false), ("idle", true) })
            {
                Step($"{theme} {state} panel={panel}: view model");
                var vm = new MainViewModel();
                vm.ShowPreview(state, panel);
                Step($"window (mic {vm.MicEnabled}, system audio {vm.SpeakerEnabled}, transcript {vm.TranscribeEnabled}, live {vm.LiveEnabled})");
                // never shown and never given a handle, so the window has no HWND at all (NoHandle checks it)
                var window = new MainWindow(vm)
                {
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                };
                NoHandle(window, "after it was built");
                var name = $"{theme.ToString().ToLowerInvariant()}-{state}{(panel ? "-live-panel" : "")}.png";
                Step($"shoot {name}");
                shots.Add(await ShootAsync(window, Path.Combine(outDir, name)));
                NoHandle(window, "after the screenshot");
                Step("close");
                window.Close();
            }
        }
        Step("done");
        var visible = VisibleTopLevelWindows();
        Console.WriteLine(string.Join(Environment.NewLine, shots));
        Console.WriteLine($"visible top-level windows of this process: {visible}");
        return visible == 0 ? 0 : 2;
    }

    private static string? _trace;
    private static DateTime _lastStep = DateTime.UtcNow;

    private static void Step(string what)
    {
        _lastStep = DateTime.UtcNow;
        try { if (_trace is not null) File.AppendAllText(_trace, $"{DateTime.Now:HH:mm:ss.fff} {what}{Environment.NewLine}"); }
        catch { }
    }

    /// <summary>
    /// Text boxes register with the Text Services Framework (input methods) through ctfmon, which does not serve an
    /// invisible desktop: the call never returns and the render hangs. Screenshots need no typing, so for this
    /// process only WPF is told no text services are installed (an internal flag; without it the render still runs
    /// on a normal desktop).
    /// </summary>
    private static bool DisableTextServices()
    {
        try
        {
            var loader = typeof(DependencyObject).Assembly.GetType("MS.Internal.TextServicesLoader"); // WindowsBase
            var field = loader?.GetField("s_servicesInstalled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (field is null) return false;
            field.SetValue(null, Enum.ToObject(field.FieldType, 2)); // InstallState.NotInstalled
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void NoHandle(Window window, string when)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException($"the main window got a window handle {when}; render-ui must never create one");
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

    /// <summary>Top-level windows of this process that are visible: always 0 (the proof the render never showed anything).</summary>
    private static int VisibleTopLevelWindows()
    {
        var pid = (uint)Environment.ProcessId;
        var n = 0;
        EnumWindows((h, _) =>
        {
            if (GetWindowThreadProcessId(h, out var p) != 0 && p == pid && IsWindowVisible(h))
            {
                n++;
                var cls = new System.Text.StringBuilder(256);
                var title = new System.Text.StringBuilder(256);
                GetClassName(h, cls, cls.Capacity);
                GetWindowText(h, title, title.Capacity);
                Step($"visible window: class '{cls}' title '{title}'");
            }
            return true;
        }, IntPtr.Zero);
        return n;
    }

    private static async Task<string> ShootAsync(Window window, string path)
    {
        var width = double.IsNaN(window.Width) ? 480 : window.Width;
        var height = Math.Min(double.IsNaN(window.Height) ? 820 : window.Height, 900);
        var root = (UIElement)window.Content;
        window.Content = null;
        var host = new Border
        {
            Width = width,
            Height = height,
            Background = Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White,
            Child = root,
            DataContext = window.DataContext,
        };
        foreach (var key in window.Resources.Keys) host.Resources[key] = window.Resources[key];
        // a message-only window (parent HWND_MESSAGE, no WS_VISIBLE): never on screen, in the taskbar or in Alt-Tab,
        // but the tree gets Loaded, so switches and other controls draw their real state
        var source = new HwndSource(new HwndSourceParameters("RecBox render")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
            Width = (int)width,
            Height = (int)height,
        });
        try
        {
            Step("  root visual");
            source.RootVisual = host;
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
            Step("  settle");
            await SettleAsync();
            host.UpdateLayout();
            await SettleAsync();
            // switches and other templates animate into their state: let those storyboards finish
            await Task.Delay(800);
            host.UpdateLayout();
            await SettleAsync();
            Step("  render");
            const double scale = 1.5;
            var bmp = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(host);
            Step("  png");
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(path)) png.Save(f);
        }
        finally
        {
            source.RootVisual = null;
            source.Dispose();
            host.Child = null;
        }
        return path;
    }

    /// <summary>Lets the queued binding, layout and Loaded work run: until the queue is idle, at most 1.5 s.</summary>
    private static async Task SettleAsync()
    {
        var idle = new TaskCompletionSource();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => idle.TrySetResult()));
        // the queue may never go idle (an animation keeps queueing render work): go on after a short wait anyway
        await Task.WhenAny(idle.Task, Task.Delay(1500));
    }
}
