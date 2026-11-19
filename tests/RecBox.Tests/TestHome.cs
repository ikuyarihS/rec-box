using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace RecBox.Tests;

/// <summary>Points RECBOX_HOME at a temp folder before any test runs, so no test ever writes the owner's real
/// %AppData%\RecBox (settings, recbox.log, worker.log, mic boost restore). Tests that set their own home restore
/// to this one, never to the real profile.</summary>
internal static class TestHome
{
    public static string Dir { get; } = Path.Combine(Path.GetTempPath(), "recbox-tests-home-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable("RECBOX_HOME", Dir);
    }
}
