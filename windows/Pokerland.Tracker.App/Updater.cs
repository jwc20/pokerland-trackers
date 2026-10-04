using Velopack;
using Velopack.Sources;

namespace Pokerland.Tracker.App;

/// <summary>Velopack updates from the GitHub releases of pokerland-trackers.</summary>
internal sealed class Updater(IFollowerLog log)
{
    public const string RepoUrl = "https://github.com/jwc20/pokerland-trackers";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, prerelease: false));
    private UpdateInfo? _downloaded;

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public bool UpdateReady => _downloaded is not null;

    /// <summary>Checks and downloads in the background; returns the version waiting to be applied, if any.</summary>
    public async Task<string?> CheckAsync()
    {
        if (!IsInstalled) return null; // running from the build output
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return null;
            await _manager.DownloadUpdatesAsync(info);
            _downloaded = info;
            log.Info($"update {info.TargetFullRelease.Version} downloaded; it applies on the next restart");
            return info.TargetFullRelease.Version.ToString();
        }
        catch (Exception e)
        {
            log.Warn($"update check failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Installs the downloaded update and restarts the app.</summary>
    public void ApplyAndRestart()
    {
        if (_downloaded is not null) _manager.ApplyUpdatesAndRestart(_downloaded);
    }
}
