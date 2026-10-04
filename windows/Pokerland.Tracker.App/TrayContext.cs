using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pokerland.Tracker.App;

/// <summary>The tray icon and the follower loop behind it. The app has no main window; settings open on demand.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly FileLog _log = new();
    private readonly Updater _updater;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _pauseItem = new("Pause uploads");
    private readonly ToolStripMenuItem _updateItem = new("Check for updates");
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };

    private CancellationTokenSource? _loop;
    private Follower? _follower;
    private SettingsForm? _settingsForm;
    private DateTimeOffset _lastUpdateCheck = DateTimeOffset.MinValue;

    public TrayContext(bool startMinimized)
    {
        _updater = new Updater(_log);
        _log.Info($"Pokerland Tracker {_updater.CurrentVersion} started");

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        _pauseItem.Click += (_, _) => TogglePause();
        menu.Items.Add(_pauseItem);
        _updateItem.Click += async (_, _) => await CheckForUpdatesAsync(interactive: true);
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.GetStockIcon(StockIconId.Application),
            Text = "Pokerland Tracker",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();

        StartupRegistry.Apply(_settings.RunAtStartup);
        if (_settings.Token == "" || !startMinimized && _settings.Roots().Count == 0) ShowSettings();
        RestartFollower();
    }

    /// <summary>(Re)starts the loop with the current settings; called after the user saves.</summary>
    public void RestartFollower()
    {
        _loop?.Cancel();
        _follower = null;
        if (_settings.Token == "" || _settings.Paused) { RefreshStatus(); return; }

        var roots = _settings.Roots();
        if (roots.Count == 0)
        {
            _log.Warn("no PokerStars HandHistory folder found; add one in Settings");
            RefreshStatus();
            return;
        }
        var version = _updater.CurrentVersion;
        var client = new ApiClient(_settings.ApiBaseUrl, _settings.Token,
            ApiClient.UserAgent(version, "windows", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()));
        _follower = new Follower(client, StateStore.Load(Paths.State), new FollowerOptions
        {
            Roots = roots,
            Platform = "windows",
            ClientVersion = version,
            Log = _log,
            Notify = (title, message) => _tray.ShowBalloonTip(10_000, title, message, ToolTipIcon.Warning),
        });
        _log.Info($"watching {string.Join("; ", roots)}");
        _loop = new CancellationTokenSource();
        var ct = _loop.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _follower.PollAsync(ct);
                    if (_follower.Status.PausedReason == "update required") await CheckForUpdatesAsync(interactive: false);
                    else if (DateTimeOffset.UtcNow - _lastUpdateCheck > UpdateCheckInterval) await CheckForUpdatesAsync(interactive: false);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    _log.Error($"poll failed: {e}");
                }
                try { await Task.Delay(_follower.PollInterval, ct); } catch (OperationCanceledException) { }
            }
        }, ct);
    }

    private void RefreshStatus()
    {
        string text;
        if (_settings.Token == "") text = "Not connected: open Settings and paste your client token";
        else if (_settings.Paused) text = "Uploads paused";
        else if (_follower is null) text = "No hand-history folder found";
        else
        {
            var s = _follower.Status;
            text = s.PausedReason is not null ? $"Paused: {s.PausedReason}"
                : s.LastError is not null ? $"Retrying: {s.LastError}"
                : s.LastUploadAt is { } at ? $"Uploading · last sent {Ago(at)} · {s.FilesTracked} files"
                : $"Watching {s.FilesTracked} files";
        }
        _statusItem.Text = text;
        _tray.Text = ("Pokerland Tracker — " + text)[..Math.Min(127, text.Length + 20)];
        _pauseItem.Text = _settings.Paused ? "Resume uploads" : "Pause uploads";
        _updateItem.Text = _updater.UpdateReady ? "Restart to update" : "Check for updates";
        _settingsForm?.ShowStatus(text);
    }

    private static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.UtcNow - at;
        return d.TotalSeconds < 60 ? $"{(int)d.TotalSeconds} s ago" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago" : $"{(int)d.TotalHours} h ago";
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings, _log, this);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
    }

    private void OpenLogFolder()
    {
        Directory.CreateDirectory(Paths.LogDir);
        Process.Start(new ProcessStartInfo(Paths.LogDir) { UseShellExecute = true });
    }

    private void TogglePause()
    {
        _settings.Paused = !_settings.Paused;
        _settings.Save();
        RestartFollower();
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        _lastUpdateCheck = DateTimeOffset.UtcNow;
        if (_updater.UpdateReady)
        {
            if (interactive || _follower?.Status.PausedReason == "update required") _updater.ApplyAndRestart();
            return;
        }
        var version = await _updater.CheckAsync();
        if (version is not null)
        {
            if (_follower?.Status.PausedReason == "update required") _updater.ApplyAndRestart();
            else _tray.ShowBalloonTip(5000, "Pokerland Tracker", $"Version {version} is ready. It installs when the tracker restarts.", ToolTipIcon.Info);
        }
        else if (interactive && !_updater.IsInstalled)
            MessageBox.Show("Updates only work for the installed version.", "Pokerland Tracker", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else if (interactive)
            MessageBox.Show($"Pokerland Tracker {_updater.CurrentVersion} is up to date.", "Pokerland Tracker", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Quit()
    {
        _loop?.Cancel();
        _tray.Visible = false;
        if (_updater.UpdateReady) _updater.ApplyAndRestart();
        ExitThread();
    }
}
