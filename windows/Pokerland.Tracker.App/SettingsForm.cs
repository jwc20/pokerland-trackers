using System.Net;
using System.Runtime.InteropServices;

namespace Pokerland.Tracker.App;

/// <summary>Token, server, folders and startup; plus a live log so the user can see what is being sent.</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly FileLog _log;
    private readonly TrayContext _tray;

    private readonly TextBox _token = new() { UseSystemPasswordChar = true, Width = 360 };
    private readonly TextBox _apiUrl = new() { Width = 360 };
    private readonly ListBox _roots = new() { Width = 480, Height = 80 };
    private readonly CheckBox _runAtStartup = new() { Text = "Start with Windows", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    private readonly TextBox _logView = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 580, Height = 180, Font = new Font(FontFamily.GenericMonospace, 8.5f) };

    public SettingsForm(AppSettings settings, FileLog log, TrayContext tray)
    {
        _settings = settings;
        _log = log;
        _tray = tray;

        Text = "Pokerland Tracker";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        _token.Text = settings.Token;
        _apiUrl.Text = settings.ApiBaseUrl;
        _runAtStartup.Checked = settings.RunAtStartup;
        RefreshRoots();

        var addRoot = new Button { Text = "Add folder…", AutoSize = true };
        addRoot.Click += (_, _) => AddRoot();
        var removeRoot = new Button { Text = "Remove", AutoSize = true };
        removeRoot.Click += (_, _) => RemoveRoot();
        _connect.Click += async (_, _) => await ConnectAsync();
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => Save(close: true);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Client token", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(Row(_token, _connect), 1, 0);
        layout.Controls.Add(new Label { Text = "From Settings in the Pokerland web app.", AutoSize = true, ForeColor = SystemColors.GrayText }, 1, 1);
        layout.Controls.Add(new Label { Text = "Server", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_apiUrl, 1, 2);
        layout.Controls.Add(new Label { Text = "Hand-history folders", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        layout.Controls.Add(Row(_roots, Column(addRoot, removeRoot)), 1, 3);
        layout.Controls.Add(_runAtStartup, 1, 4);
        layout.Controls.Add(_status, 1, 5);
        layout.Controls.Add(Row(save), 1, 6);
        layout.Controls.Add(new Label { Text = "Activity", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 7);
        layout.Controls.Add(_logView, 1, 7);
        Controls.Add(layout);

        _log.LineWritten += AppendLog;
        FormClosed += (_, _) => _log.LineWritten -= AppendLog;
    }

    public void ShowStatus(string text)
    {
        if (!IsDisposed) _status.Text = text;
    }

    private void AppendLog(string line)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (_logView.Lines.Length > 200) _logView.Lines = _logView.Lines[^150..];
            _logView.AppendText(line + Environment.NewLine);
        });
    }

    private void RefreshRoots()
    {
        _roots.Items.Clear();
        foreach (var root in Paths.DefaultRoots()) _roots.Items.Add(root + "  (detected)");
        foreach (var root in _settings.ExtraRoots) _roots.Items.Add(root);
    }

    private void AddRoot()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a PokerStars HandHistory folder" };
        if (dialog.ShowDialog(this) == DialogResult.OK && !_settings.ExtraRoots.Contains(dialog.SelectedPath))
        {
            _settings.ExtraRoots.Add(dialog.SelectedPath);
            RefreshRoots();
        }
    }

    private void RemoveRoot()
    {
        if (_roots.SelectedItem is string selected && _settings.ExtraRoots.Remove(selected)) RefreshRoots();
    }

    private async Task ConnectAsync()
    {
        var token = _token.Text.Trim();
        if (token.Length != 32)
        {
            _status.Text = "A client token is 32 characters.";
            return;
        }
        _connect.Enabled = false;
        try
        {
            using var client = new ApiClient(_apiUrl.Text.Trim(), token,
                ApiClient.UserAgent(typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", "windows",
                    RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()));
            var username = await client.GetMeAsync(CancellationToken.None);
            _status.Text = $"Connected as {username}.";
            Save(close: false);
        }
        catch (ApiStatusException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            _status.Text = "The server rejected that token.";
        }
        catch (ApiStatusException e) when (e.Status == HttpStatusCode.UpgradeRequired)
        {
            _status.Text = "This tracker version is too old; please update.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or ApiStatusException)
        {
            _status.Text = $"Could not reach the server: {e.Message}";
        }
        finally
        {
            _connect.Enabled = true;
        }
    }

    private void Save(bool close)
    {
        _settings.Token = _token.Text.Trim();
        _settings.ApiBaseUrl = _apiUrl.Text.Trim().TrimEnd('/');
        _settings.RunAtStartup = _runAtStartup.Checked;
        _settings.Save();
        StartupRegistry.Apply(_settings.RunAtStartup);
        _tray.RestartFollower();
        if (close) Close();
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        row.Controls.AddRange(controls);
        return row;
    }

    private static FlowLayoutPanel Column(params Control[] controls)
    {
        var column = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        column.Controls.AddRange(controls);
        return column;
    }
}
