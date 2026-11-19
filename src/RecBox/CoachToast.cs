using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using RecBox.Core;
using RecBox.Core.Coaching;

namespace RecBox;

/// <summary>
/// The coach's CUE as a quiet Windows notification (the kit's balloon): a tray icon appears for 10 s with the text, no sound,
/// never activates a window or takes focus. Its window is message-only (never on screen, in the taskbar or in Alt-Tab).
/// The coach already limits cues to one per 3 minutes. Never used by --render-ui or tests.
/// </summary>
public sealed class CoachToast : ICoachToast
{
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const int NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const int NiifInfo = 0x1, NiifNoSound = 0x10;
    private const uint Id = 0x52430001;

    private readonly Dispatcher _ui;
    private HwndSource? _window;
    private DispatcherTimer? _hide;
    private bool _shown;
    /// <summary>The window handle of the icon now in the tray (0 when none), read by <see cref="RemoveAll"/> from any thread.</summary>
    private IntPtr _iconWindow;

    private static readonly object Gate = new();
    private static readonly List<CoachToast> All = new();

    public CoachToast(Dispatcher ui)
    {
        _ui = ui;
        lock (Gate) All.Add(this);
    }

    /// <summary>
    /// At exit (window closed, ProcessExit, crash): takes every coach icon out of the tray now (NIM_DELETE), so no dead
    /// icon stays until the mouse passes over it. Safe from any thread and to call twice.
    /// </summary>
    public static void RemoveAll()
    {
        CoachToast[] all;
        lock (Gate) all = All.ToArray();
        foreach (var t in all)
        {
            var hwnd = Interlocked.Exchange(ref t._iconWindow, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) continue;
            try
            {
                var data = new NotifyIconData { Size = Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = Id, Tip = "", Info = "", InfoTitle = "" };
                Shell_NotifyIcon(NimDelete, ref data);
                t._shown = false;
                Log.Info("coach toast: tray icon removed at exit");
            }
            catch (Exception ex)
            {
                Log.Warn($"coach toast: icon not removed ({ex.Message})");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    public void Show(string title, string text) => _ui.BeginInvoke(() =>
    {
        try
        {
            // message-only window: parent HWND_MESSAGE, no WS_VISIBLE
            _window ??= new HwndSource(new HwndSourceParameters("RecBox coach") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            var data = new NotifyIconData
            {
                Size = Marshal.SizeOf<NotifyIconData>(),
                Window = _window.Handle,
                Id = Id,
                Flags = NifIcon | NifTip | NifInfo,
                Icon = LoadIcon(IntPtr.Zero, new IntPtr(32516)), // IDI_INFORMATION
                Tip = Loc.T("toast_title"),
                Info = text.Length > 250 ? text[..250] : text,
                InfoTitle = title.Length > 60 ? title[..60] : title,
                InfoFlags = NiifInfo | NiifNoSound,
                TimeoutOrVersion = 10000,
            };
            if (!Shell_NotifyIcon(_shown ? NimModify : NimAdd, ref data)) Log.Warn("coach toast: the notification area refused the cue");
            _shown = true;
            Interlocked.Exchange(ref _iconWindow, _window.Handle);
            _hide ??= new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => Hide(), _ui);
            _hide.Stop();
            _hide.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"coach toast failed: {ex.Message}");
        }
    });

    private void Hide()
    {
        _hide?.Stop();
        if (!_shown || _window is null) return;
        Interlocked.Exchange(ref _iconWindow, IntPtr.Zero);
        var data = new NotifyIconData { Size = Marshal.SizeOf<NotifyIconData>(), Window = _window.Handle, Id = Id, Tip = "", Info = "", InfoTitle = "" };
        Shell_NotifyIcon(NimDelete, ref data);
        _shown = false;
    }
}
