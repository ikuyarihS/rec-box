using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RecBox.Core;

/// <summary>
/// The app log: timestamped lines in &lt;home&gt;\logs\recbox.log (home = %AppData%\RecBox or RECBOX_HOME).
/// Rolling: past <see cref="MaxBytes"/> the file becomes recbox.1.log and so on, <see cref="Keep"/> files at most,
/// so it never grows without bound. The transcription worker's output goes to worker.log the same way.
/// </summary>
public static class Log
{
    public static long MaxBytes { get; set; } = 1024 * 1024;
    public static int Keep { get; set; } = 5;
    public const int KeepCrashFiles = 10;

    private static readonly object Gate = new();
    private static volatile bool _toConsole;

    public static string FilePath => Path.Combine(AppPaths.LogsDir, "recbox.log");
    public static string WorkerFilePath => Path.Combine(AppPaths.LogsDir, "worker.log");

    /// <summary>The transcription worker logs to stdout; the app captures it into worker.log.</summary>
    public static void RedirectToConsole() => _toConsole = true;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    /// <summary>One line of the transcription worker's stdout or stderr.</summary>
    public static void Worker(string line) => Append(WorkerFilePath, $"{Stamp()} {line}");

    private static string Stamp() => $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}";

    private static void Write(string level, string message)
    {
        var line = $"{Stamp()} {level} {message}";
        if (_toConsole)
        {
            try { Console.Out.WriteLine(line); Console.Out.Flush(); } catch { }
            return;
        }
        Append(FilePath, line);
    }

    private static readonly Dictionary<string, string> Fallbacks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why a log file could not be written (the first failure), null while every write works. Shown in diagnostics.</summary>
    public static string? Failure { get; private set; }

    /// <summary>Where the app log goes now: recbox.log, or the fallback file after recbox.log could not be written.</summary>
    public static string CurrentFilePath
    {
        get { lock (Gate) return Fallbacks.TryGetValue(FilePath, out var f) ? f : FilePath; }
    }

    /// <summary>
    /// Logging never breaks recording, and never stops silently (eval I8: recbox.log stayed empty for a day while
    /// worker.log was written): a file that cannot be written once moves this process to its own fallback file
    /// (recbox.pid-N.log beside it), the failure is noted in that file, in the other log and in <see cref="Failure"/>.
    /// </summary>
    private static void Append(string path, string line)
    {
        try
        {
            lock (Gate)
            {
                if (Fallbacks.TryGetValue(path, out var moved))
                {
                    TryWrite(moved, line);
                    return;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    RollIfNeeded(path, Encoding.UTF8.GetByteCount(line) + 2);
                    WriteShared(path, line);
                }
                catch (Exception ex)
                {
                    var fallback = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.pid-{Environment.ProcessId}{Path.GetExtension(path)}");
                    Fallbacks[path] = fallback;
                    var why = $"{Path.GetFileName(path)} could not be written ({ex.GetType().Name}: {ex.Message}); this process logs to {Path.GetFileName(fallback)}";
                    Failure ??= why;
                    TryWrite(fallback, $"{Stamp()} WARN {why}");
                    TryWrite(fallback, line);
                    var other = string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase) ? WorkerFilePath : FilePath;
                    if (!Fallbacks.ContainsKey(other)) TryWrite(other, $"{Stamp()} WARN {why}");
                }
            }
        }
        catch
        {
            // logging must never break recording
        }
    }

    private static void TryWrite(string path, string line)
    {
        try { WriteShared(path, line); }
        catch { }
    }

    /// <summary>Appends with read, write and delete sharing, so a reader (tail, diagnostics, a test) never blocks a write.</summary>
    private static void WriteShared(string path, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(bytes, 0, bytes.Length);
    }

    /// <summary>For tests: forget fallbacks, so the next write tries the real files again.</summary>
    internal static void ResetFallbacks()
    {
        lock (Gate)
        {
            Fallbacks.Clear();
            Failure = null;
        }
    }

    /// <summary>recbox.log -> recbox.1.log -> ... -> recbox.(Keep-1).log, the oldest dropped.</summary>
    private static void RollIfNeeded(string path, long adding)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length + adding <= MaxBytes) return;
        var dir = fi.DirectoryName!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        string Nth(int i) => Path.Combine(dir, $"{stem}.{i}{ext}");
        var keep = Math.Max(1, Keep);
        if (keep == 1)
        {
            File.Delete(path);
            return;
        }
        File.Delete(Nth(keep - 1));
        for (var i = keep - 2; i >= 1; i--)
            if (File.Exists(Nth(i))) File.Move(Nth(i), Nth(i + 1));
        File.Move(path, Nth(1));
    }

    /// <summary>
    /// A crash RecBox could not handle: writes logs\crash-&lt;time&gt;.txt with the stack trace and the system,
    /// logs it, keeps the newest <see cref="KeepCrashFiles"/> crash files. Returns the file path (null if unwritable).
    /// </summary>
    public static string? WriteCrash(string source, Exception? ex)
    {
        Error($"CRASH ({source})", ex);
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            var path = Path.Combine(AppPaths.LogsDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"RecBox crash at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine();
            sb.AppendLine(SystemInfo.Describe());
            File.WriteAllText(path, sb.ToString());
            foreach (var old in Directory.GetFiles(AppPaths.LogsDir, "crash-*.txt").OrderByDescending(f => f).Skip(KeepCrashFiles))
                File.Delete(old);
            return path;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>What the PC is, for the log and the diagnostics file: no personal data, no recordings.</summary>
public static class SystemInfo
{
    public static bool Avx => System.Runtime.Intrinsics.X86.Avx.IsSupported;
    public static bool Avx2 => System.Runtime.Intrinsics.X86.Avx2.IsSupported;
    public static bool Fma => System.Runtime.Intrinsics.X86.Fma.IsSupported;

    public static string AppVersion { get; set; } = "";

    public static string CpuName
    {
        get
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
            }
            catch { return "unknown"; }
        }
    }

    /// <summary>Display adapters from the registry (no WMI): "name (driver version)".</summary>
    public static IReadOnlyList<string> GpuNames
    {
        get
        {
            var list = new List<string>();
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (cls is null) return list;
                foreach (var name in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                {
                    try
                    {
                        using var k = cls.OpenSubKey(name);
                        if (k?.GetValue("DriverDesc") is string desc)
                            list.Add(k.GetValue("DriverVersion") is string v ? $"{desc} (driver {v})" : desc);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>(total, available) physical memory in bytes; (0, 0) when Windows does not say.</summary>
    public static (long Total, long Available) Memory()
    {
        try
        {
            var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref s)) return ((long)s.TotalPhys, (long)s.AvailPhys);
        }
        catch { }
        return (0, 0);
    }

    public static string Gb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";

    public static string Describe()
    {
        var (total, avail) = Memory();
        var sb = new StringBuilder();
        sb.AppendLine($"RecBox version: {AppVersion}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        sb.AppendLine($"Process: {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessPath}");
        sb.AppendLine($"CPU: {CpuName}, {Environment.ProcessorCount} logical cores, AVX={Avx} AVX2={Avx2} FMA={Fma}");
        sb.AppendLine($"RAM: {Gb(total)} total, {Gb(avail)} free");
        var gpus = GpuNames;
        sb.AppendLine($"GPU: {(gpus.Count == 0 ? "none found" : string.Join("; ", gpus))}");
        sb.Append($"Home: {AppPaths.Home}");
        return sb.ToString();
    }
}

/// <summary>"Copy diagnostics": one zip with the logs, crash files, settings and system info. Never audio.</summary>
public static class Diagnostics
{
    public static string DefaultDir => Path.Combine(AppPaths.Home, "diagnostics");

    public static string CreateZip(string? outDir = null)
    {
        outDir ??= DefaultDir;
        Directory.CreateDirectory(outDir);
        var zipPath = Path.Combine(outDir, $"RecBox-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        Log.Info($"writing diagnostics to {zipPath}");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            if (Directory.Exists(AppPaths.LogsDir))
            {
                foreach (var f in Directory.GetFiles(AppPaths.LogsDir)
                             .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                                         (Path.GetFileName(f).StartsWith("crash-", StringComparison.OrdinalIgnoreCase) &&
                                          f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))))
                    AddShared(zip, f, "logs/" + Path.GetFileName(f));
            }
            if (File.Exists(AppPaths.SettingsFile)) AddShared(zip, AppPaths.SettingsFile, "settings.json");
            var entry = zip.CreateEntry("system-info.txt");
            using var w = new StreamWriter(entry.Open());
            w.WriteLine(SystemInfo.Describe());
            w.WriteLine($"Log: {Log.CurrentFilePath}{(Log.Failure is { } failure ? $" (log failure: {failure})" : "")}");
        }
        return zipPath;
    }

    /// <summary>Copies a file the log may be writing right now (shared read).</summary>
    private static void AddShared(ZipArchive zip, string path, string name)
    {
        try
        {
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = zip.CreateEntry(name).Open();
            src.CopyTo(dst);
        }
        catch (Exception ex)
        {
            Log.Warn($"diagnostics: skipped {path}: {ex.Message}");
        }
    }
}
