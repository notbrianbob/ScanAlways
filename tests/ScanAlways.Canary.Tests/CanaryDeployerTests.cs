using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ScanAlways.Canary.Tests;

public class CanaryDeployerTests
{
    [Fact]
    public void Deploy_PlantsCanariesAndRecordsThem()
    {
        using var temp = new TempFolder();
        var options = temp.Options("a.docx", "b.pdf");

        var deployment = new CanaryDeployer(options, NullLogger.Instance).Deploy();

        Assert.Equal(2, deployment.Active.Count);
        Assert.All(deployment.Active, p => Assert.True(File.Exists(p)));
        Assert.Equal(4096, new FileInfo(deployment.Active[0]).Length);
        Assert.Equal(2, CanaryManifest.Load(options.ResolveManifestPath()).Files.Count);
    }

    [Fact]
    public void Deploy_WritesHeaderMatchingExtension()
    {
        using var temp = new TempFolder();
        var options = temp.Options("report.pdf");

        new CanaryDeployer(options, NullLogger.Instance).Deploy();

        var bytes = File.ReadAllBytes(Path.Combine(temp.Watched, "report.pdf"));
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public void Deploy_NeverOverwritesAFileItDidNotCreate()
    {
        using var temp = new TempFolder();
        var real = Path.Combine(temp.Watched, "a.docx");
        File.WriteAllText(real, "the user's real document");
        var options = temp.Options("a.docx");

        var deployment = new CanaryDeployer(options, NullLogger.Instance).Deploy();

        Assert.Empty(deployment.Active);
        Assert.Single(deployment.SkippedForeignFiles);
        Assert.Equal("the user's real document", File.ReadAllText(real));
    }

    [Fact]
    public void Deploy_AgainReusesIntactCanaries()
    {
        using var temp = new TempFolder();
        var options = temp.Options("a.docx");
        new CanaryDeployer(options, NullLogger.Instance).Deploy();
        var path = Path.Combine(temp.Watched, "a.docx");
        var before = File.ReadAllBytes(path);

        var second = new CanaryDeployer(options, NullLogger.Instance).Deploy();

        Assert.Single(second.Active);
        Assert.Empty(second.TamperedWhileOffline);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Deploy_ReportsCanariesTamperedWhileOffline_ThenReplants()
    {
        using var temp = new TempFolder();
        var options = temp.Options("a.docx", "b.docx");
        new CanaryDeployer(options, NullLogger.Instance).Deploy();
        var a = Path.Combine(temp.Watched, "a.docx");
        var b = Path.Combine(temp.Watched, "b.docx");
        File.SetAttributes(a, FileAttributes.Normal);
        File.WriteAllText(a, "encrypted!");
        File.SetAttributes(b, FileAttributes.Normal);
        File.Delete(b);

        var second = new CanaryDeployer(options, NullLogger.Instance).Deploy();

        Assert.Contains(second.TamperedWhileOffline, x => x.Path == Path.GetFullPath(a) && x.Change == CanaryChange.Modified);
        Assert.Contains(second.TamperedWhileOffline, x => x.Path == Path.GetFullPath(b) && x.Change == CanaryChange.Deleted);
        Assert.Equal(2, second.Active.Count);
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Verify_DetectsChangesAndDeletion()
    {
        using var temp = new TempFolder();
        var options = temp.Options("a.docx");
        var deployer = new CanaryDeployer(options, NullLogger.Instance);
        var path = deployer.Deploy().Active[0];

        Assert.Equal(CanaryState.Intact, deployer.Verify(path));

        File.SetAttributes(path, FileAttributes.Normal);
        File.AppendAllText(path, "x");
        Assert.Equal(CanaryState.Changed, deployer.Verify(path));

        File.Delete(path);
        Assert.Equal(CanaryState.Missing, deployer.Verify(path));
    }

    [Fact]
    public void Verify_TreatsAnExclusiveLockAsUnreadableNotTampered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Advisory locks on Unix don't block other readers.
        }

        using var temp = new TempFolder();
        var deployer = new CanaryDeployer(temp.Options("a.docx"), NullLogger.Instance);
        var path = deployer.Deploy().Active[0];

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(CanaryState.Unreadable, deployer.Verify(path));
        }
        Assert.Equal(CanaryState.Intact, deployer.Verify(path));
    }

    [Fact]
    public void RemoveAll_DeletesOnlyIntactCanaries()
    {
        using var temp = new TempFolder();
        var options = temp.Options("a.docx", "b.docx");
        var deployer = new CanaryDeployer(options, NullLogger.Instance);
        deployer.Deploy();
        var a = Path.Combine(temp.Watched, "a.docx");
        var b = Path.Combine(temp.Watched, "b.docx");
        File.SetAttributes(b, FileAttributes.Normal);
        File.WriteAllText(b, "tampered evidence");

        var removed = deployer.RemoveAll();

        Assert.Equal(1, removed);
        Assert.False(File.Exists(a));
        Assert.Equal("tampered evidence", File.ReadAllText(b));
    }

    [Fact]
    public void Deploy_RemovesIntactCanariesFromFoldersNoLongerConfigured()
    {
        using var temp = new TempFolder();
        var options = temp.Options("a.docx");
        new CanaryDeployer(options, NullLogger.Instance).Deploy();
        var old = Path.Combine(temp.Watched, "a.docx");

        var moved = Directory.CreateDirectory(Path.Combine(temp.Path, "elsewhere")).FullName;
        options.Folders.Clear();
        options.Folders.Add(moved);
        new CanaryDeployer(options, NullLogger.Instance).Deploy();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(moved, "a.docx")));
    }
}
