# Runs a program on a separate, invisible Windows desktop and waits for it.
# Any window it opens (by design or by mistake) is never seen on the owner's desktop,
# never in Alt-Tab or the taskbar. Use for EVERY launch of RecBox.exe during tests or gates.
# Usage: powershell -NoProfile -File tools/run-hidden.ps1 -Exe <path> [-ArgLine "<args>"] [-TimeoutSec 300]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $ArgLine = "",
    [int] $TimeoutSec = 300
)
$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class HiddenDesktop {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktop(string name, IntPtr dev, IntPtr mode, int flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
"@
$name = "recbox-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
$desk = [HiddenDesktop]::CreateDesktop($name, [IntPtr]::Zero, [IntPtr]::Zero, 0, 0x10000000, [IntPtr]::Zero)
if ($desk -eq [IntPtr]::Zero) { throw "CreateDesktop failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
try {
    $si = New-Object HiddenDesktop+STARTUPINFO
    $si.cb = [Runtime.InteropServices.Marshal]::SizeOf($si)
    $si.lpDesktop = $name
    $pi = New-Object HiddenDesktop+PROCESS_INFORMATION
    $cmd = "`"$Exe`" $ArgLine"
    if (-not [HiddenDesktop]::CreateProcess([NullString]::Value, $cmd, [IntPtr]::Zero, [IntPtr]::Zero, $false, 0x08000000, [IntPtr]::Zero, [NullString]::Value, [ref]$si, [ref]$pi)) {
        throw "CreateProcess failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    Write-Output "started pid=$($pi.dwProcessId) on hidden desktop $name"
    if ([HiddenDesktop]::WaitForSingleObject($pi.hProcess, [uint32]($TimeoutSec * 1000)) -ne 0) {
        [void][HiddenDesktop]::TerminateProcess($pi.hProcess, 1)
        Write-Output "timeout after ${TimeoutSec}s - terminated pid=$($pi.dwProcessId)"
        $code = 1
    } else {
        $c = [uint32]0; [void][HiddenDesktop]::GetExitCodeProcess($pi.hProcess, [ref]$c); $code = [int]$c
        Write-Output "exit=$code"
    }
    [void][HiddenDesktop]::CloseHandle($pi.hThread); [void][HiddenDesktop]::CloseHandle($pi.hProcess)
    exit $code
} finally {
    [void][HiddenDesktop]::CloseDesktop($desk)
}
