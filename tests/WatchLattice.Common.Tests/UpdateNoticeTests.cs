using Microsoft.Extensions.Options;
using WatchLattice.Common.Alerts;
using Xunit;

namespace WatchLattice.Common.Tests;

public sealed class UpdateNoticeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "watchlattice-update-" + Guid.NewGuid().ToString("N"));

    public UpdateNoticeTests() => Directory.CreateDirectory(_folder);

    private static UpdateNotice Notice(string url = "https://github.com/notbrianbob/WatchLattice/releases/tag/v0.2.0") =>
        new("0.1.0", "0.2.0", "WatchLattice 0.2.0", url, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Installable: true);

    [Fact]
    public void SetUpdate_RoundTripsThroughTheFeed_AndBumpsTheRevisionOnlyOnChange()
    {
        var hub = new AlertHub(Options.Create(new AlertDeliveryOptions()));
        var before = hub.Snapshot().Revision;
        var notice = Notice();

        hub.SetUpdate(notice);
        var after = hub.Snapshot().Revision;
        hub.SetUpdate(notice with { });
        Assert.True(after > before);
        Assert.Equal(after, hub.Snapshot().Revision);

        var path = Path.Combine(_folder, "alerts.json");
        AtomicJsonFile.Write(path, hub.Snapshot());
        Assert.Equal(notice, AlertSnapshot.Read(path, includeHeartbeat: false).Update);

        hub.SetUpdate(null);
        AtomicJsonFile.Write(path, hub.Snapshot());
        Assert.Null(AlertSnapshot.Read(path, includeHeartbeat: false).Update);
    }

    [Fact]
    public void Read_DropsAMalformedNotice_ButKeepsTheRestOfTheFeed()
    {
        var hub = new AlertHub(Options.Create(new AlertDeliveryOptions()));
        hub.Publish("Module", "Code", AlertSeverity.Warning, "Title", "Message");
        hub.SetUpdate(Notice() with { LatestVersion = new string('9', 500) });
        var path = Path.Combine(_folder, "alerts.json");
        AtomicJsonFile.Write(path, hub.Snapshot());

        var snapshot = AlertSnapshot.Read(path, includeHeartbeat: false);

        Assert.Null(snapshot.Update);
        Assert.Single(snapshot.Alerts);
    }

    [Theory]
    [InlineData("https://github.com/notbrianbob/WatchLattice/releases/tag/v0.2.0", true)]
    [InlineData("http://github.com/notbrianbob/WatchLattice/releases/tag/v0.2.0", false)]
    [InlineData("https://github.com.evil.example/releases", false)]
    [InlineData("https://user@github.com/releases", false)]
    [InlineData("https://github.com:8443/releases", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("calc.exe", false)]
    public void TryGetReleasePage_OnlyAcceptsHttpsGitHubLinks(string url, bool accepted) =>
        Assert.Equal(accepted, Notice(url).TryGetReleasePage(out _));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
