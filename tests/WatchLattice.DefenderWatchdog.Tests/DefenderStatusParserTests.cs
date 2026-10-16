using Xunit;

namespace WatchLattice.DefenderWatchdog.Tests;

public class DefenderStatusParserTests
{
    [Fact]
    public void Parse_ReadsAllFields()
    {
        const string json = """
            {"AMServiceEnabled":true,"AntivirusEnabled":true,"RealTimeProtectionEnabled":false,
             "BehaviorMonitorEnabled":true,"IoavProtectionEnabled":true,"OnAccessProtectionEnabled":true,
             "IsTamperProtected":true,"AMRunningMode":"Normal","AntivirusSignatureVersion":"1.419.12.0",
             "AntivirusSignatureLastUpdated":"2026-10-15T21:04:11.0000000Z"}
            """;

        var status = DefenderStatusParser.Parse(json);

        Assert.True(status.Available);
        Assert.False(status.RealTimeProtectionEnabled);
        Assert.True(status.IsTamperProtected);
        Assert.Equal("Normal", status.RunningMode);
        Assert.Equal("1.419.12.0", status.SignatureVersion);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 21, 4, 11, TimeSpan.Zero), status.SignatureLastUpdated);
    }

    [Fact]
    public void Parse_HandlesWindowsPowerShellDateFormat()
    {
        var status = DefenderStatusParser.Parse("""{"AntivirusSignatureLastUpdated":"/Date(1760000000000)/"}""");

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760000000000), status.SignatureLastUpdated);
    }

    [Fact]
    public void Parse_MissingAndNullFieldsAreUnknown()
    {
        var status = DefenderStatusParser.Parse("""{"IsTamperProtected":null}""");

        Assert.True(status.Available);
        Assert.Null(status.IsTamperProtected);
        Assert.Null(status.RealTimeProtectionEnabled);
        Assert.Null(status.SignatureLastUpdated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void Parse_BadOutputIsUnavailable(string json)
    {
        var status = DefenderStatusParser.Parse(json);

        Assert.False(status.Available);
        Assert.False(string.IsNullOrEmpty(status.Error));
    }

    [Fact]
    public void Script_IsReadOnly()
    {
        // The watchdog must never change Defender's configuration.
        Assert.Contains("Get-MpComputerStatus", PowerShellDefenderStatusProvider.Script);
        Assert.DoesNotContain("Set-Mp", PowerShellDefenderStatusProvider.Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add-Mp", PowerShellDefenderStatusProvider.Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Mp", PowerShellDefenderStatusProvider.Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_ReportsUnavailableOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var status = await new PowerShellDefenderStatusProvider().GetStatusAsync(CancellationToken.None);

        Assert.False(status.Available);
    }
}
