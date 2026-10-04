using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pokerland.Tracker;

/// <summary>Per-file upload progress ("Local state" in protocol/PROTOCOL.md).</summary>
public sealed class FileState
{
    [JsonPropertyName("stream_id")] public string StreamId { get; set; } = "";
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
    [JsonPropertyName("acked_offset")] public long AckedOffset { get; set; }
    [JsonPropertyName("registered")] public bool Registered { get; set; }
    [JsonPropertyName("last_upload_at")] public DateTimeOffset? LastUploadAt { get; set; }
    [JsonPropertyName("last_seen_at")] public DateTimeOffset LastSeenAt { get; set; }
    /// <summary>Size at the last poll; the fingerprint is only rechecked when it changes.</summary>
    [JsonPropertyName("last_size")] public long LastSize { get; set; }
}

/// <summary>Persists <see cref="FileState"/> per absolute path, written atomically.</summary>
public sealed class StateStore
{
    public const int Version = 1;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    public Dictionary<string, FileState> Files { get; private set; } = new();

    private StateStore(string path) => _path = path;

    public static StateStore Load(string path)
    {
        var store = new StateStore(path);
        if (!File.Exists(path)) return store;
        var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json)
                    ?? throw new JsonException("state file is empty");
        if (saved.Version == Version && saved.Files is not null) store.Files = saved.Files;
        return store;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Saved(Version, Files), Json));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Forgets files not seen within the retention window.</summary>
    public void Prune(DateTimeOffset now)
    {
        foreach (var (path, file) in Files.ToArray())
            if (now - file.LastSeenAt > Retention) Files.Remove(path);
    }

    private sealed record Saved(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("files")] Dictionary<string, FileState>? Files);
}
