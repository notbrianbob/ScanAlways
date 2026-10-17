using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WatchLattice.Common.Alerts;

namespace WatchLattice.Tray;

internal static class Program
{
    /// <summary>Set after an update was installed, so the new tray starts once this one has exited.</summary>
    public static bool RestartRequested { get; set; }

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--feed"))
        {
            MessageBox.Show("Usage: ScanAlways.Tray.exe [--feed <full path to alerts.json>]", "WatchLattice", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string path;
        try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(args.Length == 2 ? args[1] : AlertDeliveryOptions.DefaultFeedPath)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(ex.Message, "Invalid alert feed path", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24];
        using (var instance = new Mutex(initiallyOwned: true, @"Local\ScanAlways.Tray." + suffix, out var created))
        {
            if (!created) return;
            try { Application.Run(new TrayContext(path)); }
            finally { instance.ReleaseMutex(); }
        }

        // The single-instance mutex is closed by now, so the updated tray can claim it.
        if (RestartRequested && Environment.ProcessPath is { } executable)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            Process.Start(start)?.Dispose();
        }
    }
}
