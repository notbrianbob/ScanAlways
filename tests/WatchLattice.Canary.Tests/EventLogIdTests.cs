using WatchLattice.Common.Alerts;
using Xunit;

namespace WatchLattice.Canary.Tests;

public sealed class EventLogIdTests
{
    [Fact]
    public void EveryCanaryChange_HasItsOwnEventLogId()
    {
        foreach (var code in Enum.GetValues<CanaryChange>().Select(c => "CANARY_" + c.ToString().ToUpperInvariant()))
        {
            Assert.True(AlertEventIds.IsClassified("Canary", code), code);
        }
    }
}
