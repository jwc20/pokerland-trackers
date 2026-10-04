namespace Pokerland.Tracker.App;

/// <summary>Where the tracker keeps its files: %LocalAppData%\Pokerland, outside the Velopack install folder so updates keep them.</summary>
internal static class Paths
{
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("POKERLAND_TRACKER_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pokerland");

    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string State => Path.Combine(DataDir, "state.json");
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>PokerStars hand-history folders present on this machine, regional builds included.</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var roots = new List<string>();
        foreach (var baseDir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                 })
        {
            if (!Directory.Exists(baseDir)) continue;
            foreach (var app in Directory.EnumerateDirectories(baseDir, "PokerStars*"))
            {
                var handHistory = Path.Combine(app, "HandHistory");
                if (Directory.Exists(handHistory)) roots.Add(handHistory);
            }
        }
        return roots;
    }
}
