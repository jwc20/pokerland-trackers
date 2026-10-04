using Microsoft.Win32;

namespace Pokerland.Tracker.App;

/// <summary>Run at sign-in via HKCU\...\Run. Velopack keeps the exe at a stable path (…\Pokerland Tracker\current\), so updates do not break it.</summary>
internal static class StartupRegistry
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PokerlandTracker";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (key is null) return;
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(Name, $"\"{exe}\" --minimized");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
