using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScanAlways.Common.Alerts;
using Xunit;

namespace ScanAlways.Canary.Tests;

public sealed class SharedAlertTests
{
    [Fact]
    public void CanaryTrip_IsDeliveredToSharedAlerts()
    {
        using var temp = new TempFolder();
        var shared = new AlertHub(Options.Create(new AlertDeliveryOptions()));
        using var watcher = new CanaryWatcher(Options.Create(temp.Options()), NullLogger<CanaryWatcher>.Instance, shared);
        watcher.Arm();
        var path = Assert.Single(watcher.ActiveCanaries);
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, "changed");
        watcher.CheckIntegrityNow();
        Assert.Contains(shared.Snapshot().Alerts, a => a.Module == "Canary" && a.Resource == path && a.Severity == AlertSeverity.Critical);
    }
}
