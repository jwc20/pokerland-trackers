using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pokerland.Tracker;

/// <summary>A non-2xx reply. <see cref="AckedOffset"/> is set when the server sent one (a 409).</summary>
public sealed class ApiStatusException(HttpStatusCode status, string? detail, long? ackedOffset)
    : Exception(detail is null ? $"server replied {(int)status}" : $"server replied {(int)status}: {detail}")
{
    public HttpStatusCode Status { get; } = status;
    public string? Detail { get; } = detail;
    public long? AckedOffset { get; } = ackedOffset;

    /// <summary>Server-side or transient: worth retrying with back-off.</summary>
    public bool IsRetryable => Status == HttpStatusCode.TooManyRequests || (int)Status >= 500;
}

/// <summary>What the follower needs from the server; tests use the real client over a fake handler.</summary>
public interface ITrackerServer
{
    Task<TrackerConfig> GetConfigAsync(CancellationToken ct);
    Task<long> RegisterStreamAsync(string streamId, StreamRegistration registration, CancellationToken ct);
    Task<long> UploadChunkAsync(string streamId, long start, long end, string sha256Hex, byte[] gzip, CancellationToken ct);
}

/// <summary>HTTP client for pokerland-api's tracker endpoints (protocol/PROTOCOL.md).</summary>
public sealed class ApiClient : ITrackerServer, IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public ApiClient(string baseUrl, string token, string userAgent, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", token);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static string UserAgent(string version, string platform, string arch) => $"pokerland-tracker/{version} ({platform}; {arch})";

    public async Task<string> GetMeAsync(CancellationToken ct)
    {
        var reply = await SendAsync<MeReply>(HttpMethod.Get, "api/tracker/me/", null, ct);
        return reply.Username;
    }

    public async Task<TrackerConfig> GetConfigAsync(CancellationToken ct) =>
        await SendAsync<TrackerConfig>(HttpMethod.Get, "api/tracker/config/", null, ct);

    public async Task<long> RegisterStreamAsync(string streamId, StreamRegistration registration, CancellationToken ct)
    {
        var content = new StringContent(JsonSerializer.Serialize(registration), Encoding.UTF8, "application/json");
        var reply = await SendAsync<AckReply>(HttpMethod.Put, $"api/tracker/streams/{streamId}/", content, ct);
        return reply.AckedOffset;
    }

    public async Task<long> UploadChunkAsync(string streamId, long start, long end, string sha256Hex, byte[] gzip, CancellationToken ct)
    {
        var content = new ByteArrayContent(gzip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        content.Headers.Add("X-Chunk-End", end.ToString());
        content.Headers.Add("X-Chunk-Sha256", sha256Hex);
        var reply = await SendAsync<AckReply>(HttpMethod.Put, $"api/tracker/streams/{streamId}/chunks/{start}/", content, ct);
        return reply.AckedOffset;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            ErrorReply? error = null;
            try { error = JsonSerializer.Deserialize<ErrorReply>(body, Json); } catch (JsonException) { }
            throw new ApiStatusException(response.StatusCode, error?.Detail, error?.AckedOffset);
        }
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new JsonException("empty reply");
    }

    public void Dispose() => _http.Dispose();

    private sealed record MeReply([property: JsonPropertyName("username")] string Username);
    private sealed record AckReply([property: JsonPropertyName("acked_offset")] long AckedOffset);
    private sealed record ErrorReply(
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("acked_offset")] long? AckedOffset);
}
