using Xunit;

namespace ScanAlways.Canary.Tests;

public class CanaryTripwireTests
{
    private static readonly string Folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tripwire"));
    private static readonly string Canary = Path.Combine(Folder, "!000_Financial_Records.docx");
    private static readonly string Other = Path.Combine(Folder, "holiday.jpg");

    private static CanaryTripwire Create(TimeSpan? debounce = null, Func<DateTimeOffset>? clock = null) =>
        new(new[] { Canary }, debounce ?? TimeSpan.Zero, clock);

    [Fact]
    public void Changed_OnCanary_IsModified()
    {
        var alert = Create().Evaluate(WatcherChangeTypes.Changed, Canary);

        Assert.NotNull(alert);
        Assert.Equal(CanaryChange.Modified, alert!.Change);
        Assert.Equal(Canary, alert.Path);
    }

    [Fact]
    public void Deleted_OnCanary_IsDeleted()
    {
        var alert = Create().Evaluate(WatcherChangeTypes.Deleted, Canary);

        Assert.Equal(CanaryChange.Deleted, alert?.Change);
    }

    [Fact]
    public void Created_OnCanaryPath_IsTreatedAsReplacement()
    {
        var alert = Create().Evaluate(WatcherChangeTypes.Created, Canary);

        Assert.Equal(CanaryChange.Modified, alert?.Change);
    }

    [Fact]
    public void Renamed_AwayFromCanary_ReportsNewName()
    {
        var alert = Create().Evaluate(WatcherChangeTypes.Renamed, Canary + ".locked", Canary);

        Assert.NotNull(alert);
        Assert.Equal(CanaryChange.Renamed, alert!.Change);
        Assert.Equal(Canary, alert.Path);
        Assert.Contains(".locked", alert.Detail);
    }

    [Fact]
    public void Renamed_OntoCanary_IsReplacement()
    {
        var alert = Create().Evaluate(WatcherChangeTypes.Renamed, Canary, Path.Combine(Folder, "tmp123"));

        Assert.Equal(CanaryChange.Modified, alert?.Change);
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Changed)]
    [InlineData(WatcherChangeTypes.Created)]
    [InlineData(WatcherChangeTypes.Deleted)]
    public void EventsOnOtherFiles_AreIgnored(WatcherChangeTypes type)
    {
        Assert.Null(Create().Evaluate(type, Other));
    }

    [Fact]
    public void RenameBetweenOtherFiles_IsIgnored()
    {
        Assert.Null(Create().Evaluate(WatcherChangeTypes.Renamed, Other + ".bak", Other));
    }

    [Fact]
    public void DuplicateAlerts_AreDebounced_ThenAllowedAgain()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tripwire = Create(TimeSpan.FromSeconds(5), () => now);

        Assert.NotNull(tripwire.Evaluate(WatcherChangeTypes.Changed, Canary));
        Assert.Null(tripwire.Evaluate(WatcherChangeTypes.Changed, Canary));

        // A different kind of change on the same canary is still reported.
        Assert.NotNull(tripwire.Evaluate(WatcherChangeTypes.Deleted, Canary));

        now = now.AddSeconds(6);
        Assert.NotNull(tripwire.Evaluate(WatcherChangeTypes.Changed, Canary));
    }

    [Fact]
    public void Report_IgnoresPathsThatAreNotCanaries()
    {
        Assert.Null(Create().Report(Other, CanaryChange.Modified, "test"));
    }
}
