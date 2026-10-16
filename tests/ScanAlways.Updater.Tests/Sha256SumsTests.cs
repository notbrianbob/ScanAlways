using Xunit;

namespace ScanAlways.Updater.Tests;

public class Sha256SumsTests
{
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Fact]
    public void Parse_ReadsSha256sumAndBinaryModeLines()
    {
        var sums = Sha256Sums.Parse($"{Hash}  WatchLattice-0.2.0-win-x64.zip\r\n{Hash.ToUpperInvariant()} *./other.zip\n\n# comment\n");

        Assert.Equal(Hash, sums["WatchLattice-0.2.0-win-x64.zip"]);
        Assert.Equal(Hash, sums["other.zip"]);
    }

    [Theory]
    [InlineData("not a checksum line")]
    [InlineData("abc123  short.zip")]
    public void Parse_RejectsMalformedFiles(string text) =>
        Assert.Throws<InvalidDataException>(() => Sha256Sums.Parse(text));

    [Fact]
    public void Parse_RejectsConflictingEntries() =>
        Assert.Throws<InvalidDataException>(() => Sha256Sums.Parse($"{Hash}  a.zip\n{new string('0', 64)}  a.zip\n"));

    [Fact]
    public void Compute_And_Matches_AgreeOnKnownValue()
    {
        using var stream = new MemoryStream("test"u8.ToArray());
        var actual = Sha256Sums.Compute(stream);

        Assert.Equal(Hash, actual);
        Assert.True(Sha256Sums.Matches(actual, Hash.ToUpperInvariant()));
        Assert.False(Sha256Sums.Matches(actual, new string('0', 64)));
        Assert.False(Sha256Sums.Matches(actual, "short"));
    }
}
