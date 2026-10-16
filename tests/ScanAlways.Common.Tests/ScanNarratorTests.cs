using ScanAlways.Common;
using Xunit;

namespace ScanAlways.Common.Tests;

public class ScanNarratorTests
{
    [Fact]
    public void FileLine_IncludesPathAndIsDeterministicWithSeed()
    {
        var a = new ScanNarrator(seed: 42);
        var b = new ScanNarrator(seed: 42);

        var lineA = a.FileLine(@"C:\Users\sample\report.docx");
        var lineB = b.FileLine(@"C:\Users\sample\report.docx");

        Assert.Equal(lineA, lineB);
        Assert.Contains("report.docx", lineA);
    }

    [Fact]
    public void ProgressLine_AlwaysReportsZeroThreats()
    {
        var narrator = new ScanNarrator(seed: 1);
        var stats = new ScanStatistics();
        stats.RecordRead(1024);

        var line = narrator.ProgressLine(stats);

        Assert.Contains("0 threats found", line);
        Assert.Contains("1 items checked", line);
    }

    [Theory]
    [InlineData(512, "512.0 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    public void FormatBytes_IsHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, ScanNarrator.FormatBytes(bytes));
    }
}
