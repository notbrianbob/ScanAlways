using Xunit;

namespace WatchLattice.Updater.Tests;

public class UpdateEvaluatorTests
{
    private static readonly ReleaseVersion Current = ReleaseVersion.Parse("0.2.0");

    [Theory]
    [InlineData("0.1.9")]
    [InlineData("0.2.0")]
    public void FindNewer_IgnoresOlderAndEqualReleases(string version) =>
        Assert.Null(UpdateEvaluator.FindNewer(Current, Releases.Make(version), Releases.Repository));

    [Fact]
    public void FindNewer_ReturnsAnInstallableCandidate()
    {
        var candidate = UpdateEvaluator.FindNewer(Current, Releases.Make("0.3.0"), Releases.Repository);

        Assert.NotNull(candidate);
        Assert.Equal(ReleaseVersion.Parse("0.3.0"), candidate!.Version);
        Assert.True(candidate.Installable);
        Assert.Equal("WatchLattice-0.3.0-win-x64.zip", candidate.Package!.Name);
    }

    [Fact]
    public void FindNewer_ReportsButRefusesAReleaseWithoutChecksums()
    {
        var candidate = UpdateEvaluator.FindNewer(Current, Releases.Make("0.3.0", checksums: false), Releases.Repository);

        Assert.NotNull(candidate);
        Assert.False(candidate!.Installable);
        Assert.Contains("SHA256SUMS.txt", candidate.Problem);
    }

    [Fact]
    public void FindNewer_RefusesDownloadsHostedElsewhere()
    {
        var release = Releases.Make("0.3.0", packageUrl: "https://github.com/someone-else/fork/releases/download/v0.3.0/WatchLattice-0.3.0-win-x64.zip");

        var candidate = UpdateEvaluator.FindNewer(Current, release, Releases.Repository);

        Assert.False(candidate!.Installable);
    }

    [Fact]
    public void FindNewer_SkipsDraftsAndPrereleasesForStableBuilds()
    {
        Assert.Null(UpdateEvaluator.FindNewer(Current, Releases.Make("0.3.0") with { Draft = true }, Releases.Repository));
        Assert.Null(UpdateEvaluator.FindNewer(Current, Releases.Make("0.3.0-beta.1") with { Prerelease = true }, Releases.Repository));
        Assert.NotNull(UpdateEvaluator.FindNewer(ReleaseVersion.Parse("0.3.0-beta.1"), Releases.Make("0.3.0-beta.2"), Releases.Repository));
    }

    [Fact]
    public void FindNewer_RejectsTagsThatAreNotVersions() =>
        Assert.Throws<InvalidDataException>(() =>
            UpdateEvaluator.FindNewer(Current, Releases.Make("0.3.0") with { TagName = "nightly" }, Releases.Repository));
}
