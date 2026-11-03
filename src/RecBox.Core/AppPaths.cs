namespace RecBox.Core;

/// <summary>
/// Where RecBox keeps its settings, logs and models. Set RECBOX_HOME to move all of it
/// (also moves the default recordings folder under it).
/// </summary>
public static class AppPaths
{
    public static string? HomeOverride =>
        Environment.GetEnvironmentVariable("RECBOX_HOME") is { Length: > 0 } h ? h : null;

    public static string Home => HomeOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecBox");

    public static string SettingsFile => Path.Combine(Home, "settings.json");
    public static string LogsDir => Path.Combine(Home, "logs");
    public static string ModelsDir => Path.Combine(Home, "models");

    public static string DefaultRecordingsDir => HomeOverride is { } h
        ? Path.Combine(h, "recordings")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RecBox");
}
