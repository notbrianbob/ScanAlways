using Xunit;

namespace WatchLattice.Canary.Tests;

public class CanaryOptionsTests
{
    [Fact]
    public void ResolveFileNames_UsesDefaultsWhenEmpty()
    {
        var names = new CanaryOptions().ResolveFileNames();

        Assert.Equal(CanaryOptions.DefaultFileNames, names);
    }

    [Fact]
    public void ResolveFileNames_RejectsPathsAndInvalidNames()
    {
        var options = new CanaryOptions
        {
            FileNames = { "good.docx", "../escape.docx", "sub/dir.docx", "..", "  " },
        };

        var names = options.ResolveFileNames();

        Assert.Equal(new[] { "good.docx" }, names);
    }

    [Fact]
    public void ResolveFolders_SkipsFoldersThatDoNotExist()
    {
        using var temp = new TempFolder();
        var options = new CanaryOptions
        {
            Folders = { temp.Watched, Path.Combine(temp.Path, "missing") },
        };

        var folders = options.ResolveFolders();

        Assert.Equal(new[] { Path.GetFullPath(temp.Watched) }, folders);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "missing")));
    }

    [Fact]
    public void DataPaths_DefaultUnderWatchLatticeFolder()
    {
        var options = new CanaryOptions();

        Assert.EndsWith(Path.Combine("ScanAlways", "canary-alerts.log"), options.ResolveAlertLogPath());
        Assert.EndsWith(Path.Combine("ScanAlways", "canary-manifest.json"), options.ResolveManifestPath());
    }
}
