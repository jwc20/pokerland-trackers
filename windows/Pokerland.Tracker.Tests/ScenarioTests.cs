using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Pokerland.Tracker.Tests;

/// <summary>Runs protocol/scenarios/*.json; see scenarios/README.md for the format.</summary>
public sealed class ScenarioTests
{
    public static IEnumerable<object[]> Scenarios() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "scenarios"), "*.json")
            .OrderBy(f => f)
            .Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario(string name)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "scenarios", name + ".json"));
        var scenario = JsonSerializer.Deserialize<ScenarioFile>(json, JsonOptions)!;
        var config = scenario.Config is null ? new TrackerConfig() : scenario.Config.Value.Deserialize<TrackerConfig>(JsonOptions)!;

        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        var statePath = Path.Combine(temp.Path, "state.json");
        var server = new FakeServerHandler(config);
        var client = new ApiClient("http://fake", "0123456789abcdef0123456789abcdef", ApiClient.UserAgent("0.1.0", "windows", "x64"), server);
        var clock = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

        Follower follower = null!;
        void Start() => follower = new Follower(client, StateStore.Load(statePath), new FollowerOptions
        {
            Roots = [root],
            Platform = "windows",
            ClientVersion = "0.1.0",
            Now = () => clock,
            Jitter = d => d,
        });
        Start();

        foreach (var step in scenario.Steps)
        {
            if (step.Write is { } write)
            {
                var path = Path.Combine(root, write.File.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(Encoding.UTF8.GetBytes(write.Text));
            }
            else if (step.Truncate is { } truncate)
            {
                using var fs = new FileStream(Path.Combine(root, truncate.File), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                fs.SetLength(truncate.Size);
            }
            else if (step.Delete is { } delete) File.Delete(Path.Combine(root, delete.File));
            else if (step.Tick > 0)
                for (var i = 0; i < step.Tick; i++)
                {
                    await follower.PollAsync(CancellationToken.None);
                    clock += follower.PollInterval;
                }
            else if (step.Advance > 0) clock += TimeSpan.FromSeconds(step.Advance);
            else if (step.LoseState) { File.Delete(statePath); Start(); }
            else if (step.Restart) Start();
            else if (step.Server is { } s)
            {
                server.FailNext += s.Fail;
                if (s.Status != 0) { server.OverrideStatus = (HttpStatusCode)s.Status; server.OverrideTimes = s.Times; }
                if (s.Rewind is { } rewind) server.Rewind(rewind.File, rewind.AckedOffset);
            }
            else Assert.Fail($"unknown step: {JsonSerializer.Serialize(step)}");
        }

        Assert.Empty(server.Problems);
        CheckEveryFile(root, server);
        foreach (var (file, want) in scenario.Expect.Chunks ?? new())
        {
            var current = CurrentStream(root, server, file);
            Assert.True(current is not null, $"{file}: no current stream");
            Assert.True(current!.Chunks.Count == want, $"{file}: {current.Chunks.Count} chunks, want {want}");
        }
        foreach (var (file, want) in scenario.Expect.Streams ?? new())
            Assert.True(server.StreamsFor(file).Count() == want, $"{file}: {server.StreamsFor(file).Count()} streams, want {want}");
        if (scenario.Expect.RequestsAtMost is { } atMost)
            Assert.True(server.Requests <= atMost, $"{server.Requests} requests, want at most {atMost}");
    }

    /// <summary>The invariant every scenario must satisfy: the server has exactly each file's complete lines.</summary>
    private static void CheckEveryFile(string root, FakeServerHandler server)
    {
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!path.EndsWith(".txt", StringComparison.Ordinal))
            {
                Assert.True(!server.StreamsFor(rel).Any(), $"{rel}: streams for a file that should be ignored");
                continue;
            }
            var content = File.ReadAllBytes(path);
            var cut = Array.LastIndexOf(content, (byte)'\n');
            if (cut < 0)
            {
                Assert.True(!server.StreamsFor(rel).Any(), $"{rel}: streams for a file with no complete line");
                continue;
            }
            var current = CurrentStream(root, server, rel);
            Assert.True(current is not null, $"{rel}: no stream with the file's fingerprint");
            var got = current!.Bytes();
            Assert.True(got.AsSpan().SequenceEqual(content.AsSpan(0, cut + 1)),
                $"{rel}: server has {got.Length} bytes, file has {cut + 1} complete bytes");
        }
    }

    private static FakeServerHandler.Stream? CurrentStream(string root, FakeServerHandler server, string rel)
    {
        var content = File.ReadAllBytes(Path.Combine(root, rel));
        var end = Array.IndexOf(content, (byte)'\n');
        if (end < 0) return null;
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(content.AsSpan(0, end + 1)));
        var matches = server.StreamsFor(rel).Where(s => s.Fingerprint == fingerprint).ToList();
        Assert.True(matches.Count <= 1, $"{rel}: two streams share the fingerprint");
        return matches.SingleOrDefault();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record ScenarioFile(string Name, JsonElement? Config, List<Step> Steps, Expectations Expect);

    private sealed record Expectations(
        Dictionary<string, int>? Chunks,
        Dictionary<string, int>? Streams,
        [property: JsonPropertyName("requests_at_most")] int? RequestsAtMost);

    private sealed record Step(
        WriteStep? Write,
        TruncateStep? Truncate,
        DeleteStep? Delete,
        int Tick,
        int Advance,
        bool Restart,
        [property: JsonPropertyName("lose_state")] bool LoseState,
        ServerStep? Server);

    private sealed record WriteStep(string File, string Text);
    private sealed record TruncateStep(string File, long Size);
    private sealed record DeleteStep(string File);
    private sealed record ServerStep(int Fail, int Status, int Times, RewindStep? Rewind);
    private sealed record RewindStep(string File, [property: JsonPropertyName("acked_offset")] long AckedOffset);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("pokerland-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
