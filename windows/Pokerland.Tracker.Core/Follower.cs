using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace Pokerland.Tracker;

public interface IFollowerLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public sealed class FollowerOptions
{
    public required IReadOnlyList<string> Roots { get; init; }
    public string Include { get; init; } = "*.txt";
    public required string Platform { get; init; }
    public required string ClientVersion { get; init; }
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
    /// <summary>Stretches a back-off delay; the default adds up to 50% at random.</summary>
    public Func<TimeSpan, TimeSpan> Jitter { get; init; } = d => d + TimeSpan.FromTicks(Random.Shared.NextInt64(d.Ticks / 2 + 1));
    public IFollowerLog Log { get; init; } = new NullLog();
    /// <summary>Tells the user something they must act on (bad token, update needed).</summary>
    public Action<string, string> Notify { get; init; } = (_, _) => { };

    private sealed class NullLog : IFollowerLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }
}

public sealed record FollowerStatus
{
    public DateTimeOffset? LastPollAt { get; init; }
    public DateTimeOffset? LastUploadAt { get; init; }
    public string? LastError { get; init; }
    public string? PausedReason { get; init; }
    public DateTimeOffset? PausedUntil { get; init; }
    public int FilesTracked { get; init; }
    public long BytesUploaded { get; init; }
}

/// <summary>The tracker loop from protocol/PROTOCOL.md: identify files, upload their new bytes in order.</summary>
public sealed class Follower
{
    private const string Source = "pokerstars";
    private const long MinChunkBytes = 4 * 1024;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InvalidTokenWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UpgradeWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan ConfigRefresh = TimeSpan.FromHours(6);

    private readonly ITrackerServer _server;
    private readonly StateStore _store;
    private readonly FollowerOptions _opts;

    private DateTimeOffset? _configFetchedAt;
    private long _chunkLimit;
    private TimeSpan _backoff;
    private DateTimeOffset? _retryAt;
    private DateTimeOffset? _pausedUntil;
    private FollowerStatus _status = new();

    public TrackerConfig Config { get; private set; } = new();
    public FollowerStatus Status => _status with { FilesTracked = _store.Files.Count };
    public TimeSpan PollInterval => Config.PollInterval;

    public Follower(ITrackerServer server, StateStore store, FollowerOptions options)
    {
        _server = server;
        _store = store;
        _opts = options;
        _chunkLimit = Config.MaxReadBytes;
    }

    /// <summary>Polls until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollAsync(ct);
            try { await Task.Delay(PollInterval, ct); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>One pass over every file. Failures are retried on later polls.</summary>
    public async Task PollAsync(CancellationToken ct)
    {
        var now = _opts.Now();
        _status = _status with { LastPollAt = now };
        await RefreshConfigAsync(now, ct);

        foreach (var path in Scan())
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await PollFileAsync(path, ct);
            }
            catch (IOException e)
            {
                _opts.Log.Warn($"file skipped: {path}: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                _opts.Log.Warn($"file skipped: {path}: {e.Message}");
            }
        }
        _store.Prune(now);
        try { _store.Save(); } catch (IOException e) { _opts.Log.Error($"saving state failed: {e.Message}"); }
    }

    private async Task RefreshConfigAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_configFetchedAt is { } at && now - at < ConfigRefresh) return;
        if (!CanSend(now)) return;
        try
        {
            var config = await _server.GetConfigAsync(ct);
            _configFetchedAt = now;
            if (_chunkLimit > config.MaxReadBytes || _chunkLimit == Config.MaxReadBytes) _chunkLimit = config.MaxReadBytes;
            Config = config;
        }
        catch (Exception e) when (e is ApiStatusException or HttpRequestException or TaskCanceledException)
        {
            HandleError(e, now);
        }
    }

    private IEnumerable<string> Scan()
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in _opts.Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, _opts.Include, options)) yield return path;
        }
    }

    private async Task PollFileAsync(string path, CancellationToken ct)
    {
        var now = _opts.Now();
        var size = new FileInfo(path).Length;
        _store.Files.TryGetValue(path, out var file);
        file ??= new FileState();
        file.LastSeenAt = now;

        // Identify: a new file, a shrunk file, or a changed first line.
        if (file.StreamId == "" || size < file.AckedOffset || size != file.LastSize)
        {
            var fingerprint = Fingerprint.Compute(path);
            if (fingerprint is null) return; // no complete first line yet
            if (fingerprint != file.Fingerprint)
                file = new FileState { Fingerprint = fingerprint, StreamId = Fingerprint.StreamId(fingerprint), LastSeenAt = now };
            else if (size < file.AckedOffset)
                file.Registered = false; // rewritten identically: ask the server where it stands
            file.LastSize = size;
            _store.Files[path] = file;
        }

        if (!CanSend(now)) return;
        if (!file.Registered)
        {
            try
            {
                var acked = await _server.RegisterStreamAsync(file.StreamId,
                    new StreamRegistration(Source, _opts.Platform, _opts.ClientVersion, PathHint(path), file.Fingerprint), ct);
                Recovered();
                file.Registered = true;
                file.AckedOffset = acked;
                _store.Save();
            }
            catch (Exception e) when (e is ApiStatusException or HttpRequestException or TaskCanceledException)
            {
                HandleError(e, now);
                return;
            }
        }

        var pending = size - file.AckedOffset;
        var due = file.LastUploadAt is null || pending >= Config.FlushBytes || now - file.LastUploadAt >= Config.FlushInterval;
        if (pending <= 0 || !due) return;
        await UploadBacklogAsync(path, file, size, ct);
    }

    /// <summary>Sends every complete line past the acked offset, chunk by chunk.</summary>
    private async Task UploadBacklogAsync(string path, FileState file, long size, CancellationToken ct)
    {
        while (file.AckedOffset < size && CanSend(_opts.Now()))
        {
            var data = ReadChunk(path, file.AckedOffset, Math.Min(_chunkLimit, Config.MaxReadBytes));
            if (data.Length == 0) return; // only a partial line remains
            var gz = Compress(data);
            if (gz.Length > Config.MaxChunkBytes)
            {
                if (!ShrinkChunks()) throw new IOException($"a {data.Length}-byte chunk still compresses past max_chunk_bytes");
                continue;
            }
            var start = file.AckedOffset;
            var end = start + data.Length;
            var sha = Convert.ToHexStringLower(SHA256.HashData(data));
            long acked;
            try
            {
                acked = await _server.UploadChunkAsync(file.StreamId, start, end, sha, gz, ct);
            }
            catch (Exception e) when (e is ApiStatusException or HttpRequestException or TaskCanceledException)
            {
                if (HandleUploadError(e, file, _opts.Now())) continue;
                return;
            }
            var now = _opts.Now();
            Recovered();
            file.AckedOffset = acked;
            file.LastUploadAt = now;
            _status = _status with { LastUploadAt = now, BytesUploaded = _status.BytesUploaded + data.Length };
            _store.Save();
            _opts.Log.Info($"uploaded {Path.GetFileName(path)} [{start}, {end}) as {gz.Length} gzip bytes");
        }
    }

    /// <summary>Returns whether the loop should try the file again right away.</summary>
    private bool HandleUploadError(Exception error, FileState file, DateTimeOffset now)
    {
        if (error is ApiStatusException status)
        {
            switch (status.Status)
            {
                case HttpStatusCode.Conflict when status.AckedOffset is { } serverOffset:
                    if (serverOffset == file.AckedOffset)
                    {
                        // The server has different bytes at this offset: retrying cannot help.
                        _opts.Log.Error($"server holds different bytes at offset {serverOffset}: {status.Detail}");
                        _status = _status with { LastError = status.Message };
                        return false;
                    }
                    _opts.Log.Info($"server is at offset {serverOffset}, we were at {file.AckedOffset}; following it");
                    file.AckedOffset = serverOffset;
                    return true;
                case HttpStatusCode.NotFound:
                    file.Registered = false; // re-register on the next poll
                    return false;
                case HttpStatusCode.RequestEntityTooLarge:
                    return ShrinkChunks();
                case HttpStatusCode.BadRequest:
                    _opts.Log.Warn($"chunk rejected; will re-read the file: {status.Detail}");
                    return false;
            }
        }
        HandleError(error, now);
        return false;
    }

    /// <summary>Applies the PROTOCOL.md table for replies that affect every request.</summary>
    private void HandleError(Exception error, DateTimeOffset now)
    {
        _status = _status with { LastError = error.Message };
        if (error is ApiStatusException status)
        {
            switch (status.Status)
            {
                case HttpStatusCode.Unauthorized:
                    Pause(now, InvalidTokenWait, "client token rejected");
                    _opts.Notify("Pokerland Tracker", "Your client token was rejected. Paste the token from your Settings page into the tracker's settings.");
                    return;
                case HttpStatusCode.UpgradeRequired:
                    Pause(now, UpgradeWait, "update required");
                    _opts.Notify("Pokerland Tracker", "This tracker version is no longer supported. Please update.");
                    return;
            }
            if (!status.IsRetryable)
            {
                _opts.Log.Warn($"request failed: {error.Message}");
                return;
            }
        }
        _backoff = _backoff == TimeSpan.Zero ? InitialBackoff : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
        _retryAt = now + _opts.Jitter(_backoff);
        _opts.Log.Warn($"request failed; retrying in {_backoff}: {error.Message}");
    }

    private void Recovered()
    {
        _backoff = TimeSpan.Zero;
        _retryAt = null;
        _pausedUntil = null;
        _status = _status with { LastError = null, PausedReason = null, PausedUntil = null };
    }

    private void Pause(DateTimeOffset now, TimeSpan duration, string reason)
    {
        _pausedUntil = now + duration;
        _status = _status with { PausedReason = reason, PausedUntil = _pausedUntil };
        _opts.Log.Warn($"uploads paused ({reason}) until {_pausedUntil:O}");
    }

    private bool CanSend(DateTimeOffset now) => !(now < _pausedUntil) && !(now < _retryAt);

    private bool ShrinkChunks()
    {
        if (_chunkLimit <= MinChunkBytes) return false;
        _chunkLimit = Math.Max(_chunkLimit / 2, MinChunkBytes);
        _opts.Log.Info($"chunk too large; halving to {_chunkLimit} bytes");
        return true;
    }

    private string PathHint(string path)
    {
        foreach (var root in _opts.Roots)
        {
            var rel = Path.GetRelativePath(root, path);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) return rel.Replace('\\', '/');
        }
        return Path.GetFileName(path);
    }

    /// <summary>Up to <paramref name="limit"/> bytes from <paramref name="offset"/>, cut at the last newline.</summary>
    private static byte[] ReadChunk(string path, long offset, long limit)
    {
        using var stream = Fingerprint.OpenShared(path);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[limit];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        var cut = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(read - 1, 0), read);
        return cut < 0 ? [] : buffer[..(cut + 1)];
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(data);
        return output.ToArray();
    }
}
