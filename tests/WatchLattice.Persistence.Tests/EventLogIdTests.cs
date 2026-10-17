using WatchLattice.Common.Alerts;
using WatchLattice.Persistence.Verification;
using Xunit;

namespace WatchLattice.Persistence.Tests;

public sealed class EventLogIdTests
{
    [Fact]
    public void EveryPersistenceAlert_HasItsOwnEventLogId()
    {
        var codes = Enum.GetValues<PersistenceChangeKind>().Select(k => "ENTRY_" + k.ToString().ToUpperInvariant())
            .Concat(new[] { "BASELINE_CREATED", "SOURCE_UNAVAILABLE", "SOURCE_RECOVERED" })
            .Concat(new[]
            {
                PersistenceVerificationCoordinator.PassedCode, PersistenceVerificationCoordinator.NeedsReviewCode,
                PersistenceVerificationCoordinator.SuspiciousCode, PersistenceVerificationCoordinator.StoreFailedCode,
            });
        foreach (var code in codes) Assert.True(AlertEventIds.IsClassified("Persistence", code), code);
    }
}
