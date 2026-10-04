using Velopack;

namespace Pokerland.Tracker.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Must run first: handles Velopack's install/update/uninstall hooks and exits for them.
        VelopackApp.Build().Run();

        using var single = new Mutex(initiallyOwned: true, "Local\\PokerlandTracker-7f3b9c2e", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Pokerland Tracker is already running. Look for it in the system tray.", "Pokerland Tracker",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(startMinimized: args.Contains("--minimized")));
    }
}
