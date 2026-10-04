using Xunit;

namespace Pokerland.Tracker.Tests;

public sealed class FingerprintTests
{
    [Fact]
    public void StreamIdMatchesTheServerAndTheGoTracker()
    {
        // The same vector as pokerland-api's tests and mac/internal/follower/fingerprint_test.go.
        var id = Fingerprint.StreamId("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");

        Assert.Equal("67158b66-6466-5697-b139-fdc563e2573c", id);
    }

    [Fact]
    public void FingerprintIsTheFirstLineIncludingTheBom()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "a.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .."PokerStars Hand #1: x\nSeat 1: Alice\n"u8]);
        var withBom = Fingerprint.Compute(path);
        File.WriteAllBytes(path, "PokerStars Hand #1: x\nSeat 1: Alice\n"u8.ToArray());
        var withoutBom = Fingerprint.Compute(path);
        File.WriteAllBytes(path, "PokerStars Hand #1: x"u8.ToArray());
        var partial = Fingerprint.Compute(path);

        Assert.NotEqual(withBom, withoutBom);
        Assert.Equal(64, withBom!.Length);
        Assert.Null(partial);
    }

    [Fact]
    public void StateRoundTripsAndPrunes()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "nested", "state.json");
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var store = StateStore.Load(path);
        store.Files["C:\\a.txt"] = new FileState { StreamId = "id", Fingerprint = "fp", AckedOffset = 42, Registered = true, LastSeenAt = now };
        store.Files["C:\\old.txt"] = new FileState { StreamId = "old", LastSeenAt = now.AddDays(-31) };
        store.Save();

        var loaded = StateStore.Load(path);
        loaded.Prune(now);

        Assert.Equal(42, loaded.Files["C:\\a.txt"].AckedOffset);
        Assert.True(loaded.Files["C:\\a.txt"].Registered);
        Assert.False(loaded.Files.ContainsKey("C:\\old.txt"));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
