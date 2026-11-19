using System.Runtime.InteropServices;
using System.Text;

namespace RecBox;

/// <summary>Tells whether this thread runs on the interactive desktop ("Default", the one people see) or on a
/// separate desktop such as the invisible one tools/run-hidden.ps1 creates for tests.</summary>
internal static class HiddenDesktop
{
    private const int UOI_NAME = 2;

    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(int threadId);
    [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder? info, int length, out int needed);

    public static string? CurrentDesktopName()
    {
        var desk = GetThreadDesktop(GetCurrentThreadId());
        if (desk == IntPtr.Zero) return null;
        var sb = new StringBuilder(256);
        return GetUserObjectInformation(desk, UOI_NAME, sb, sb.Capacity * 2, out _) ? sb.ToString() : null;
    }

    /// <summary>True only for a desktop named like the test launcher's ("recbox-test-..."); unknown counts as visible.</summary>
    public static bool IsCurrentThreadOnHiddenDesktop() =>
        CurrentDesktopName() is { } n && n.StartsWith("recbox-test-", StringComparison.OrdinalIgnoreCase);
}
