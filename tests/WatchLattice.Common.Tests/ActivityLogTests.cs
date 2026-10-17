using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WatchLattice.Common.Activity;
using WatchLattice.Common.Alerts;
using Xunit;

namespace WatchLattice.Common.Tests;

public sealed class ActivityLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "watchlattice-activity-" + Guid.NewGuid().ToString("N"));

    public ActivityLogTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void Provider_KeepsInformationAndAbove_WithoutHttpChatter()
    {
        var log = new ActivityLog();
        using var provider = new ActivityLoggerProvider(log);
        var module = provider.CreateLogger("WatchLattice.Canary.CanaryWatcher");

        module.LogDebug("too detailed");
        module.LogInformation("Planted {Count} canaries", 3);
        module.LogError(new IOException("disk gone"), "Integrity check failed");
        provider.CreateLogger("System.Net.Http.HttpClient.Default.LogicalHandler").LogInformation("GET https://api.github.com");

        var entries = log.Snapshot().Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("CanaryWatcher", entries[0].Source);
        Assert.Equal("Planted 3 canaries", entries[0].Message);
        Assert.Equal(LogLevel.Error, entries[1].Level);
        Assert.Equal("Integrity check failed (IOException: disk gone)", entries[1].Message);
    }

    [Fact]
    public void Log_IsBounded_AndRestoresEarlierLinesFirst()
    {
        var log = new ActivityLog(limit: 3);
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) log.Add(start.AddSeconds(i), LogLevel.Information, "Hub", "line " + i);
        Assert.Equal(new[] { "line 2", "line 3", "line 4" }, log.Snapshot().Entries.Select(e => e.Message));

        var next = new ActivityLog(limit: 3);
        next.Add(start.AddSeconds(10), LogLevel.Warning, "Hub", "new session");
        next.Restore(log.Snapshot().Entries);
        Assert.Equal(new[] { "line 3", "line 4", "new session" }, next.Snapshot().Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Delivery_WritesActivityNextToTheFeed_AndRestoresItAfterARestart()
    {
        var options = new AlertDeliveryOptions { FeedPath = Path.Combine(_folder, "alerts.json") };
        var first = new ActivityLog();
        first.Add(DateTimeOffset.UtcNow, LogLevel.Information, "ModuleSupervisor", "Starting module Canary");
        using (var service = new AlertDeliveryService(new AlertHub(Options.Create(options)), Options.Create(options),
            NullLogger<AlertDeliveryService>.Instance, first))
        {
            await service.StartAsync(default);
            await service.StopAsync(default);
        }

        var path = ActivitySnapshot.PathFor(options.FeedPath);
        Assert.Equal("Starting module Canary", Assert.Single(ActivitySnapshot.Read(path).Entries).Message);

        var second = new ActivityLog();
        using (var service = new AlertDeliveryService(new AlertHub(Options.Create(options)), Options.Create(options),
            NullLogger<AlertDeliveryService>.Instance, second))
        {
            await service.StartAsync(default);
            await service.StopAsync(default);
        }
        Assert.Single(second.Snapshot().Entries);
    }

    [Fact]
    public void Read_RejectsUnknownVersions()
    {
        var path = Path.Combine(_folder, "alerts.json.activity.json");
        File.WriteAllText(path, "{\"version\":99,\"sessionId\":\"" + Guid.NewGuid() + "\",\"entries\":[]}");
        Assert.Throws<InvalidDataException>(() => ActivitySnapshot.Read(path));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
