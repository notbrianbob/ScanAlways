using Xunit;

namespace ScanAlways.FirewallWatchdog.Tests;

public class FirewallStatusParserTests
{
    [Fact]
    public void Parse_ReadsProfilesNetworksAndRules()
    {
        const string json = """
            {"Profiles":[{"Name":"Domain","Enabled":"True","DefaultInboundAction":"NotConfigured"},
                         {"Name":"Private","Enabled":"False","DefaultInboundAction":"Block"},
                         {"Name":"Public","Enabled":"True","DefaultInboundAction":"Allow"}],
             "ActiveCategories":["DomainAuthenticated","Public"],
             "Rules":[{"Id":"CoreNet-DHCP-In","DisplayName":"Core Networking - DHCP (DHCP-In)","Profile":"Any"}],
             "RulesError":null}
            """;

        var status = FirewallStatusParser.Parse(json);

        Assert.True(status.Available);
        Assert.Equal(3, status.Profiles.Count);
        Assert.Null(status.Profiles[0].DefaultInboundAction);
        Assert.False(status.Profiles[1].Enabled);
        Assert.True(status.Profiles[2].InboundAllowsByDefault);
        Assert.Equal(new[] { "Domain", "Public" }, status.ActiveProfiles);
        var rule = Assert.Single(status.InboundAllowRules!);
        Assert.Equal("CoreNet-DHCP-In", rule.Id);
        Assert.Null(status.RulesError);
    }

    [Fact]
    public void Parse_AcceptsWindowsPowerShellArrayWrapperAndSingleObjects()
    {
        const string json = """
            {"Profiles":{"value":[{"Name":"Public","Enabled":"True","DefaultInboundAction":"Block"}],"Count":1},
             "ActiveCategories":"Private",
             "Rules":{"Id":"only-rule","DisplayName":"Only rule","Profile":"Public"}}
            """;

        var status = FirewallStatusParser.Parse(json);

        Assert.Equal("Public", Assert.Single(status.Profiles).Name);
        Assert.Equal(new[] { "Private" }, status.ActiveProfiles);
        Assert.Equal("only-rule", Assert.Single(status.InboundAllowRules!).Id);
    }

    [Fact]
    public void Parse_NullsMeanUnknownOrNotRequested()
    {
        var status = FirewallStatusParser.Parse(
            """{"Profiles":[{"Name":"Public","Enabled":"True","DefaultInboundAction":"Block"}],"ActiveCategories":null,"Rules":null,"RulesError":"denied"}""");

        Assert.Null(status.ActiveProfiles);
        Assert.Null(status.InboundAllowRules);
        Assert.Equal("denied", status.RulesError);
        Assert.True(status.MayBeActive("Public"));
    }

    [Fact]
    public void Parse_EmptyActiveList_MeansNoNetworkInUse()
    {
        var status = FirewallStatusParser.Parse(
            """{"Profiles":[{"Name":"Public","Enabled":"False","DefaultInboundAction":"Block"}],"ActiveCategories":[]}""");

        Assert.NotNull(status.ActiveProfiles);
        Assert.False(status.MayBeActive("Public"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"Profiles":[]}""")]
    [InlineData("""{"Profiles":[{"Name":"Mystery","Enabled":"True"}]}""")]
    public void Parse_BadOutputIsUnavailable(string json)
    {
        var status = FirewallStatusParser.Parse(json);

        Assert.False(status.Available);
        Assert.False(string.IsNullOrEmpty(status.Error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Script_IsReadOnly(bool includeRules)
    {
        // The watchdog must never change the firewall's configuration.
        var script = PowerShellFirewallStatusProvider.BuildScript(includeRules);

        Assert.Contains("Get-NetFirewallProfile", script);
        Assert.Contains("-PolicyStore ActiveStore", script);
        foreach (var verb in new[] { "Set-", "New-", "Remove-", "Enable-", "Disable-", "Copy-", "Rename-", "netsh" })
        {
            Assert.DoesNotContain(verb, script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Provider_ReportsUnavailableOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var status = await new PowerShellFirewallStatusProvider().GetStatusAsync(true, CancellationToken.None);

        Assert.False(status.Available);
    }

    [Fact]
    public async Task Provider_ReadsTheRealFirewallOnWindows()
    {
        // A live, read-only smoke test of the PowerShell query on Windows CI
        // runners; elsewhere there is no firewall to ask.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var status = await new PowerShellFirewallStatusProvider().GetStatusAsync(true, CancellationToken.None);

        Assert.True(status.Available, status.Error);
        Assert.Equal(3, status.Profiles.Count);
        Assert.True(status.InboundAllowRules is not null, status.RulesError);
    }
}
