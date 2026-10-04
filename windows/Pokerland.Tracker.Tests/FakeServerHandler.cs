using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pokerland.Tracker.Tests;

/// <summary>Speaks the protocol in-process, with the fault injection the scenarios need.</summary>
public sealed partial class FakeServerHandler(TrackerConfig config) : HttpMessageHandler
{
    public sealed class Stream
    {
        public required string Id { get; init; }
        public required string Fingerprint { get; init; }
        public string PathHint { get; set; } = "";
        public long Acked { get; set; }
        public List<(long Start, long End, byte[] Data)> Chunks { get; } = new();

        /// <summary>The chunks concatenated; a gap is a harness bug.</summary>
        public byte[] Bytes()
        {
            using var all = new MemoryStream();
            long expect = 0;
            foreach (var (start, end, data) in Chunks)
            {
                if (start != expect) throw new InvalidOperationException("fake server stored a gap");
                all.Write(data);
                expect = end;
            }
            return all.ToArray();
        }
    }

    public Dictionary<string, Stream> Streams { get; } = new();
    public int Requests { get; private set; }
    public int FailNext { get; set; }
    public HttpStatusCode? OverrideStatus { get; set; }
    public int OverrideTimes { get; set; }
    public List<string> Problems { get; } = new();

    [GeneratedRegex(@"^/api/tracker/streams/([0-9a-f-]+)/$")] private static partial Regex StreamPath();
    [GeneratedRegex(@"^/api/tracker/streams/([0-9a-f-]+)/chunks/(\d+)/$")] private static partial Regex ChunkPath();

    public IEnumerable<Stream> StreamsFor(string pathHint) => Streams.Values.Where(s => s.PathHint == pathHint);

    /// <summary>Forgets everything past <paramref name="offset"/> for the path's stream.</summary>
    public void Rewind(string pathHint, long offset)
    {
        var stream = StreamsFor(pathHint).Single();
        stream.Chunks.RemoveAll(c => c.End > offset);
        stream.Acked = offset;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        if (request.Headers.Authorization?.Scheme != "Token") Problems.Add($"missing client token on {request.Method} {request.RequestUri}");
        if (!(request.Headers.UserAgent.ToString().StartsWith("pokerland-tracker/"))) Problems.Add($"missing tracker User-Agent on {request.RequestUri}");
        if (FailNext > 0)
        {
            FailNext--;
            return Reply(HttpStatusCode.ServiceUnavailable, new { detail = "injected failure" });
        }
        if (OverrideTimes > 0 && OverrideStatus is { } status)
        {
            OverrideTimes--;
            return Reply(status, new { detail = "injected status" });
        }

        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/tracker/config/") return Reply(HttpStatusCode.OK, config);
        if (path == "/api/tracker/me/") return Reply(HttpStatusCode.OK, new { username = "alice" });
        if (request.Method == HttpMethod.Put && StreamPath().Match(path) is { Success: true } m)
            return await RegisterAsync(request, m.Groups[1].Value, ct);
        if (request.Method == HttpMethod.Put && ChunkPath().Match(path) is { Success: true } c)
            return await UploadAsync(request, c.Groups[1].Value, long.Parse(c.Groups[2].Value), ct);
        return Reply(HttpStatusCode.NotFound, new { detail = "no such route" });
    }

    private async Task<HttpResponseMessage> RegisterAsync(HttpRequestMessage request, string id, CancellationToken ct)
    {
        var reg = JsonSerializer.Deserialize<StreamRegistration>(await request.Content!.ReadAsStringAsync(ct));
        if (reg is null || reg.Fingerprint.Length != 64 || reg.Source == "" || reg.Platform == "" || reg.ClientVersion == "")
            return Reply(HttpStatusCode.BadRequest, new { detail = "bad registration" });
        if (!Streams.TryGetValue(id, out var stream))
        {
            Streams[id] = new Stream { Id = id, Fingerprint = reg.Fingerprint, PathHint = reg.PathHint };
            return Reply(HttpStatusCode.Created, new { stream_id = id, acked_offset = 0 });
        }
        if (stream.Fingerprint != reg.Fingerprint) return Reply(HttpStatusCode.Conflict, new { detail = "fingerprint mismatch" });
        stream.PathHint = reg.PathHint;
        return Reply(HttpStatusCode.OK, new { stream_id = id, acked_offset = stream.Acked });
    }

    private async Task<HttpResponseMessage> UploadAsync(HttpRequestMessage request, string id, long start, CancellationToken ct)
    {
        if (!Streams.TryGetValue(id, out var stream)) return Reply(HttpStatusCode.NotFound, new { detail = "unknown stream" });
        if (request.Content?.Headers.ContentType?.MediaType != "application/gzip")
            return Reply(HttpStatusCode.UnsupportedMediaType, new { detail = "expected application/gzip" });
        if (!long.TryParse(request.Content.Headers.GetValues("X-Chunk-End").Single(), out var end) || end <= start)
            return Reply(HttpStatusCode.BadRequest, new { detail = "bad X-Chunk-End" });
        var body = await request.Content.ReadAsByteArrayAsync(ct);
        if (body.Length > config.MaxChunkBytes) return Reply(HttpStatusCode.RequestEntityTooLarge, new { detail = "too large" });
        byte[] data;
        try
        {
            using var gz = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
            using var out_ = new MemoryStream();
            gz.CopyTo(out_);
            data = out_.ToArray();
        }
        catch (InvalidDataException)
        {
            return Reply(HttpStatusCode.BadRequest, new { detail = "not gzip" });
        }
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        if (data.Length != end - start) return Reply(HttpStatusCode.BadRequest, new { detail = "length mismatch" });
        if (sha != request.Content.Headers.GetValues("X-Chunk-Sha256").Single().ToLowerInvariant())
            return Reply(HttpStatusCode.BadRequest, new { detail = "hash mismatch" });
        if (data[^1] != (byte)'\n') return Reply(HttpStatusCode.BadRequest, new { detail = "no trailing newline" });
        if (start != stream.Acked)
        {
            if (stream.Chunks.Any(c => c.Start == start && c.End == end && c.Data.AsSpan().SequenceEqual(data)))
                return Reply(HttpStatusCode.OK, new { acked_offset = stream.Acked });
            return Reply(HttpStatusCode.Conflict, new { detail = "expected another offset", acked_offset = stream.Acked });
        }
        stream.Chunks.Add((start, end, data));
        stream.Acked = end;
        return Reply(HttpStatusCode.Accepted, new { acked_offset = end });
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}
