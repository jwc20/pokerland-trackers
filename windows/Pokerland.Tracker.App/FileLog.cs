namespace Pokerland.Tracker.App;

/// <summary>Appends to logs\tracker.log, keeping one previous file once it passes 5 MB.</summary>
internal sealed class FileLog : IFollowerLog
{
    private const long RotateAt = 5 * 1024 * 1024;
    private readonly string _path = Path.Combine(Paths.LogDir, "tracker.log");
    private readonly Lock _lock = new();

    /// <summary>The most recent lines, for the tray window.</summary>
    public event Action<string>? LineWritten;

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Paths.LogDir);
                if (File.Exists(_path) && new FileInfo(_path).Length > RotateAt)
                    File.Move(_path, Path.ChangeExtension(_path, ".1.log"), overwrite: true);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never take the tracker down.
            }
        }
        LineWritten?.Invoke(line);
    }
}
