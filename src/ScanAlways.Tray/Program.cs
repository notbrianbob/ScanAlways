using System.Security.Cryptography;
using System.Text;
using ScanAlways.Common.Alerts;

namespace ScanAlways.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--feed"))
        {
            MessageBox.Show("Usage: ScanAlways.Tray.exe [--feed <full path to alerts.json>]", "ScanAlways", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        using var instance = new Mutex(initiallyOwned: true, @"Local\ScanAlways.Tray." + suffix, out var created);
        if (!created) return;
        try { Application.Run(new TrayContext(path)); }
        finally { instance.ReleaseMutex(); }
    }
}
