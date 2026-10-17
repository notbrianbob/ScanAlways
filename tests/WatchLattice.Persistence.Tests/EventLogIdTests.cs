using WatchLattice.Common.Alerts;
using Xunit;

namespace WatchLattice.Persistence.Tests;

public sealed class EventLogIdTests
{
    [Fact]
    public void EveryPersistenceAlert_HasItsOwnEventLogId()
    {
        var codes = Enum.GetValues<PersistenceChangeKind>().Select(k => "ENTRY_" + k.ToString().ToUpperInvariant())
            .Concat(new[] { "BASELINE_CREATED", "SOURCE_UNAVAILABLE", "SOURCE_RECOVERED" });
        foreach (var code in codes) Assert.True(AlertEventIds.IsClassified("Persistence", code), code);
    }
}
