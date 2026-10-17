using System.IO.Compression;
using Xunit;

namespace WatchLattice.Updater.Tests;

public class PackageExtractorTests
{
    private static readonly string[] Required = { "Hub/ScanAlways.Protection.Service.exe", "Tray/ScanAlways.Tray.exe" };

    [Fact]
    public void Extract_WritesTheReleaseLayout()
    {
        using var temp = new TempDirectory();

        PackageExtractor.Extract(new MemoryStream(Releases.Package("v2")), temp.Combine("out"), Required);

        Assert.Equal("hub v2", File.ReadAllText(temp.Combine("out", "Hub", "ScanAlways.Protection.Service.exe")));
        Assert.Equal("tray v2", File.ReadAllText(temp.Combine("out", "Tray", "ScanAlways.Tray.exe")));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("Hub/../../evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("/etc/evil")]
    [InlineData("C:/Windows/evil.dll")]
    public void Extract_RefusesPathsThatEscapeTheStagingFolder(string name)
    {
        using var temp = new TempDirectory();
        var zip = Releases.Package("v2", new Dictionary<string, string> { [name] = "payload" });

        Assert.Throws<InvalidDataException>(() => PackageExtractor.Extract(new MemoryStream(zip), temp.Combine("out"), Required));
        Assert.False(File.Exists(temp.Combine("evil.dll")));
    }

    [Fact]
    public void Extract_AcceptsBackslashSeparatorsFromWindowsZipTools()
    {
        using var temp = new TempDirectory();
        var zip = Releases.Package("v2", new Dictionary<string, string> { ["Hub\\runtimes\\win\\native.dll"] = "native" });

        PackageExtractor.Extract(new MemoryStream(zip), temp.Combine("out"), Required);

        Assert.Equal("native", File.ReadAllText(temp.Combine("out", "Hub", "runtimes", "win", "native.dll")));
    }

    [Fact]
    public void Extract_RequiresTheHubAndTrayExecutables()
    {
        using var temp = new TempDirectory();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) zip.CreateEntry("Hub/readme.txt");
        buffer.Position = 0;

        var error = Assert.Throws<InvalidDataException>(() => PackageExtractor.Extract(buffer, temp.Combine("out"), Required));
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public void Extract_RejectsCorruptArchives()
    {
        using var temp = new TempDirectory();

        Assert.Throws<InvalidDataException>(() =>
            PackageExtractor.Extract(new MemoryStream("not a zip"u8.ToArray()), temp.Combine("out"), Required));
    }
}
