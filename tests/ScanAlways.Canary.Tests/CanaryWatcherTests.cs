using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ScanAlways.Canary.Tests;

public class CanaryWatcherTests
{
    private static CanaryWatcher Create(CanaryOptions options) =>
        new(Options.Create(options), NullLogger<CanaryWatcher>.Instance);

    [Fact]
    public void IntegrityCheck_CatchesModificationOnce()
    {
        using var temp = new TempFolder();
        using var watcher = Create(temp.Options("a.docx"));
        var alerts = new List<CanaryAlert>();
        watcher.AlertRaised += a => { lock (alerts) alerts.Add(a); };
        watcher.Arm();
        var path = watcher.ActiveCanaries.Single();

        watcher.CheckIntegrityNow();
        lock (alerts) Assert.Empty(alerts);

        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, "encrypted");
        watcher.CheckIntegrityNow();
        watcher.CheckIntegrityNow();

        lock (alerts)
        {
            Assert.Single(alerts);
            Assert.Equal(path, alerts[0].Path);
        }
    }

    [Fact]
    public void IntegrityCheck_CatchesDeletion()
    {
        using var temp = new TempFolder();
        using var watcher = Create(temp.Options("a.docx"));
        CanaryAlert? alert = null;
        watcher.AlertRaised += a => alert ??= a;
        watcher.Arm();
        var path = watcher.ActiveCanaries.Single();

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        watcher.CheckIntegrityNow();

        Assert.NotNull(alert);
        Assert.Equal(path, alert!.Path);
    }

    [Fact]
    public async Task FileWatcher_RaisesAlertWhenCanaryIsRenamed()
    {
        using var temp = new TempFolder();
        using var watcher = Create(temp.Options("a.docx"));
        var tripped = new TaskCompletionSource<CanaryAlert>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.AlertRaised += a => tripped.TrySetResult(a);
        watcher.Arm();
        var path = watcher.ActiveCanaries.Single();

        File.Move(path, path + ".locked");

        var alert = await tripped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(path, alert.Path);
    }

    [Fact]
    public async Task FileWatcher_IgnoresOtherFilesInTheFolder()
    {
        using var temp = new TempFolder();
        using var watcher = Create(temp.Options("a.docx"));
        var alerts = 0;
        watcher.AlertRaised += _ => Interlocked.Increment(ref alerts);
        watcher.Arm();

        var other = Path.Combine(temp.Watched, "notes.txt");
        await File.WriteAllTextAsync(other, "hello");
        File.Delete(other);
        await Task.Delay(500);

        Assert.Equal(0, Volatile.Read(ref alerts));
    }

    [Fact]
    public void Arm_DoesNotAlertOnItsOwnPlanting()
    {
        using var temp = new TempFolder();
        using var watcher = Create(temp.Options("a.docx", "b.xlsx"));
        var alerts = 0;
        watcher.AlertRaised += _ => Interlocked.Increment(ref alerts);

        watcher.Arm();
        watcher.CheckIntegrityNow();

        Assert.Equal(2, watcher.ActiveCanaries.Count);
        Assert.Equal(0, Volatile.Read(ref alerts));
    }

    [Fact]
    public void AlertsAreWrittenToTheAlertLog()
    {
        using var temp = new TempFolder();
        var options = temp.Options("a.docx");
        using (var watcher = Create(options))
        {
            watcher.Arm();
            var path = watcher.ActiveCanaries.Single();
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            watcher.CheckIntegrityNow();
        }

        var log = File.ReadAllText(options.ResolveAlertLogPath());
        Assert.Contains("CANARY TRIPPED", log);
    }
}
