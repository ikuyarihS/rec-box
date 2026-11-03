using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using RecBox.Core;
using Velopack;
using Velopack.Sources;

namespace RecBox.Services;

/// <summary>
/// Velopack auto-update from GitHub Releases: checks the feed, downloads in the background, restarts on request.
/// Setup.exe installs and the Portable zip both update this way. A copy Velopack does not manage (a bare
/// publish folder) only learns that a newer release exists and gets a link to its page.
/// </summary>
public sealed class UpdateService
{
    public const string DefaultFeed = "https://github.com/ikuyarihS/rec-box";

    private UpdateManager? _manager;
    private UpdateInfo? _pending;
    private string? _url;

    public static string? BakedFeedUrl =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateUrl")?.Value is { Length: > 0 } url ? url : null;

    /// <summary>settings.json UpdateUrl, else the feed baked in at build time, else the RecBox GitHub repo.</summary>
    public static string FeedUrl(string? overrideUrl) =>
        overrideUrl is { Length: > 0 } ? overrideUrl : BakedFeedUrl ?? DefaultFeed;

    public string CurrentVersion
    {
        get
        {
            try
            {
                if (_manager?.IsInstalled == true && _manager.CurrentVersion is { } v) return v.ToString();
            }
            catch { }
            return Program.AppVersion;
        }
    }

    public bool UpdateReady => _pending is not null;

    /// <summary>Set when a newer release exists that this copy cannot install by itself.</summary>
    public string? ReleasePageUrl { get; private set; }

    public void Configure(string? overrideUrl)
    {
        var url = FeedUrl(overrideUrl);
        _url = url;
        _manager = null;
        _pending = null;
        ReleasePageUrl = null;
        try
        {
            IUpdateSource source = IsGithub(url)
                ? new GithubSource(url, null, false)
                : new SimpleWebSource(url);
            _manager = new UpdateManager(source);
            Log.Info($"update feed {url} (installed={_manager.IsInstalled}, portable={_manager.IsPortable})");
        }
        catch (Exception ex)
        {
            Log.Error($"update feed {url} unusable", ex);
        }
    }

    private static bool IsGithub(string url) => url.Contains("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a short status line for the settings page.</summary>
    public async Task<string> CheckAndDownloadAsync()
    {
        ReleasePageUrl = null;
        if (_manager is null) return "No update feed is set for this build.";
        Log.Info($"update check: feed {_url}, current {CurrentVersion}, installed={_manager.IsInstalled}");
        if (!_manager.IsInstalled) return await CheckReleasePageAsync();
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                Log.Info("update check: up to date");
                return $"You have the latest version ({CurrentVersion}).";
            }
            Log.Info($"update check: {info.TargetFullRelease.Version} available, downloading");
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

    /// <summary>Not a Velopack-managed copy: ask GitHub for the latest release and offer its page.</summary>
    private async Task<string> CheckReleasePageAsync()
    {
        if (_url is null || !IsGithub(_url)) return "Updates apply to installed copies (run Setup.exe).";
        try
        {
            var parts = new Uri(_url).AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2) return "Updates apply to installed copies (run Setup.exe).";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RecBox-updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/latest"));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var page = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            if (IsNewer(tag, CurrentVersion))
            {
                ReleasePageUrl = page ?? $"{_url.TrimEnd('/')}/releases/latest";
                Log.Info($"update check (not installed): {tag} available at {ReleasePageUrl}");
                return $"New version {tag.TrimStart('v')} available. Download it from the release page.";
            }
            Log.Info($"update check (not installed): latest is {tag}, up to date");
            return $"You have the latest version ({CurrentVersion}).";
        }
        catch (Exception ex)
        {
            Log.Error("update check (release page) failed", ex);
            return "Could not reach GitHub. Recording still works; try again later.";
        }
    }

    public static bool IsNewer(string tag, string current)
    {
        static Version? Parse(string s) =>
            Version.TryParse(s.Trim().TrimStart('v', 'V').Split('-', '+')[0], out var v) ? v : null;
        var latest = Parse(tag);
        var mine = Parse(current);
        return latest is not null && (mine is null || latest > mine);
    }

    public void RestartToApply()
    {
        if (_manager is null || _pending is null) return;
        Log.Info($"restarting to apply update {_pending.TargetFullRelease.Version}");
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
