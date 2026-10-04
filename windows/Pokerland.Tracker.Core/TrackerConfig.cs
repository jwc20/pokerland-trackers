using System.Text.Json.Serialization;

namespace Pokerland.Tracker;

/// <summary>What <c>GET /api/tracker/config</c> returns; the defaults apply until the first fetch.</summary>
public sealed record TrackerConfig
{
    [JsonPropertyName("min_version")] public string MinVersion { get; init; } = "0.0.0";
    [JsonPropertyName("poll_interval_seconds")] public int PollIntervalSeconds { get; init; } = 2;
    [JsonPropertyName("flush_interval_seconds")] public int FlushIntervalSeconds { get; init; } = 10;
    [JsonPropertyName("flush_bytes")] public long FlushBytes { get; init; } = 256 * 1024;
    [JsonPropertyName("max_read_bytes")] public long MaxReadBytes { get; init; } = 1024 * 1024;
    [JsonPropertyName("max_chunk_bytes")] public long MaxChunkBytes { get; init; } = 4 * 1024 * 1024;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
    public TimeSpan FlushInterval => TimeSpan.FromSeconds(FlushIntervalSeconds);
}

/// <summary>The body of <c>PUT /api/tracker/streams/{id}</c>.</summary>
public sealed record StreamRegistration(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("client_version")] string ClientVersion,
    [property: JsonPropertyName("path_hint")] string PathHint,
    [property: JsonPropertyName("fingerprint")] string Fingerprint);
