using WatchLattice.Common.Alerts;
using Xunit;

namespace WatchLattice.Canary.Tests;

public sealed class EventLogIdTests
{
    [Fact]
    public void EveryCanaryChange_HasItsOwnEventLogId()
    {
        foreach (var change in Enum.GetValues<CanaryChange>())
        {
            var code = "CANARY_" + change.ToString().ToUpperInvariant();
            Assert.True(AlertEventIds.IsClassified("Canary", code), code);
        }
    }
}
