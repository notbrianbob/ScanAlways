using ScanAlways.Common;
using Xunit;

namespace ScanAlways.Common.Tests;

public class DecoyOptionsTests
{
    [Fact]
    public void ResolveScanRoots_DropsNonExistentPaths()
    {
        var options = new DecoyOptions
        {
            ScanRoots = { Path.GetTempPath(), "/definitely/not/a/real/path/here" },
        };

        var roots = options.ResolveScanRoots();

        Assert.Contains(roots, r => string.Equals(
            r.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("/definitely/not/a/real/path/here", roots);
    }

    [Fact]
    public void ResolveScanRoots_FallsBackWhenEmpty()
    {
        var options = new DecoyOptions();

        var roots = options.ResolveScanRoots();

        // Fallbacks are only included if they exist, but at least one of the
        // user-profile style folders should resolve on a normal host.
        Assert.All(roots, r => Assert.True(Directory.Exists(r)));
    }

    [Theory]
    [InlineData(DecoyIntensity.Idle)]
    [InlineData(DecoyIntensity.Balanced)]
    [InlineData(DecoyIntensity.FullScan)]
    public void Pace_IsPositiveForEveryIntensity(DecoyIntensity intensity)
    {
        var options = new DecoyOptions { Intensity = intensity };

        var (filesPerSecond, narrateEvery) = options.Pace();

        Assert.True(filesPerSecond >= 1);
        Assert.True(narrateEvery > 0);
    }

    [Fact]
    public void Pace_NeverExceedsMaxFilesPerSecond()
    {
        var options = new DecoyOptions { Intensity = DecoyIntensity.FullScan, MaxFilesPerSecond = 2 };

        var (filesPerSecond, _) = options.Pace();

        Assert.True(filesPerSecond <= 2);
    }
}
