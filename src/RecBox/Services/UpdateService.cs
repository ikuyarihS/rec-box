using System.Reflection;
using RecBox.Core;
using Velopack;
using Velopack.Sources;

namespace RecBox.Services;

/// <summary>Velopack auto-update: checks the feed, downloads in the background, restarts on request.</summary>
public sealed class UpdateService
{
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public static string? BakedFeedUrl =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateUrl")?.Value is { Length: > 0 } url ? url : null;

    public string CurrentVersion
    {
        get
        {
            try
            {
                if (_manager?.IsInstalled == true && _manager.CurrentVersion is { } v) return v.ToString();
            }
            catch { }
            return Core.Audio.SessionFinalizer.AppVersion;
        }
    }

    public bool UpdateReady => _pending is not null;

    public void Configure(string? overrideUrl)
    {
        var url = overrideUrl is { Length: > 0 } ? overrideUrl : BakedFeedUrl;
        _manager = null;
        _pending = null;
        if (url is null) return;
        try
        {
            IUpdateSource source = url.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(url, null, false)
                : new SimpleWebSource(url);
            _manager = new UpdateManager(source);
        }
        catch (Exception ex)
        {
            Log.Error($"update feed {url} unusable", ex);
        }
    }

    /// <summary>Returns a short status line for the settings page.</summary>
    public async Task<string> CheckAndDownloadAsync()
    {
        if (_manager is null) return "No update feed is set for this build.";
        if (!_manager.IsInstalled) return "Updates apply to installed copies (run Setup.exe).";
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return $"You have the latest version ({CurrentVersion}).";
            await _manager.DownloadUpdatesAsync(info);
            _pending = info;
            Log.Info($"update {info.TargetFullRelease.Version} downloaded");
            return $"Version {info.TargetFullRelease.Version} is ready. Restart to finish.";
        }
        catch (Exception ex)
        {
            Log.Error("update check failed", ex);
            return "Could not reach the update server. Recording still works; try again later.";
        }
    }

    public void RestartToApply()
    {
        if (_manager is null || _pending is null) return;
        Log.Info($"restarting to apply update {_pending.TargetFullRelease.Version}");
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
