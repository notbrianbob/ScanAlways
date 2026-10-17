using System.Xml;
using WatchLattice.Persistence.Verification;
using Xunit;

namespace WatchLattice.Persistence.Tests.Verification;

public sealed class WindowsPathTests
{
    private static readonly SystemPaths Paths = TaskScenarios.Paths;

    [Theory]
    [InlineData(@"\Microsoft\Windows\SoftwareProtectionPlatform\SvcRestartTask", true)]
    [InlineData(@"\TopLevel", true)]
    [InlineData(@"\Folder\Task with spaces", true)]
    [InlineData(@"Microsoft\Windows\Task", false)]
    [InlineData(@"\", false)]
    [InlineData(@"\Folder\", false)]
    [InlineData(@"\..\Windows", false)]
    [InlineData(@"\a\\b", false)]
    [InlineData(@"\a\b:stream", false)]
    [InlineData("\\a\\b\u0000", false)]
    [InlineData(@"\a/b", false)]
    public void TaskPaths_AreValidated(string path, bool valid) => Assert.Equal(valid, WindowsPath.IsValidTaskPath(path));

    [Theory]
    [InlineData("{b1aebb5d-ead9-4476-b375-9c3ed9f32afc}", "{B1AEBB5D-EAD9-4476-B375-9C3ED9F32AFC}")]
    [InlineData(" B1AEBB5D-EAD9-4476-B375-9C3ED9F32AFC ", "{B1AEBB5D-EAD9-4476-B375-9C3ED9F32AFC}")]
    public void Clsids_AreNormalized(string input, string expected)
    {
        Assert.True(WindowsPath.TryNormalizeClsid(input, out var clsid));
        Assert.Equal(expected, clsid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"{B1AEBB5D-EAD9-4476-B375-9C3ED9F32AFC}\..\..\Run")]
    [InlineData("(B1AEBB5D-EAD9-4476-B375-9C3ED9F32AFC)")]
    [InlineData("B1AEBB5DEAD94476B3759C3ED9F32AFC")]
    public void MalformedClsids_AreRejected(string? input) => Assert.False(WindowsPath.TryNormalizeClsid(input, out _));

    [Theory]
    [InlineData(@"%SystemRoot%\system32\sppcext.dll", PathResolution.Resolved, @"C:\Windows\system32\sppcext.dll")]
    [InlineData(@"""C:\Program Files\Vendor\app.exe""", PathResolution.Resolved, @"C:\Program Files\Vendor\app.exe")]
    [InlineData(@"c:/windows/system32/../system32/./cmd.exe", PathResolution.Resolved, @"C:\windows\system32\cmd.exe")]
    [InlineData(@"\\?\C:\Windows\x.exe", PathResolution.Resolved, @"C:\Windows\x.exe")]
    [InlineData(@"\\server\share\x.exe", PathResolution.Network, @"\\server\share\x.exe")]
    [InlineData(@"\\?\UNC\server\share\x.exe", PathResolution.Network, @"\\?\UNC\server\share\x.exe")]
    [InlineData(@"\\.\pipe\x", PathResolution.Invalid, @"\\.\pipe\x")]
    [InlineData(@"C:\x.txt:evil.exe", PathResolution.Invalid, @"C:\x.txt:evil.exe")]
    [InlineData(@"C:\x*.exe", PathResolution.Invalid, @"C:\x*.exe")]
    [InlineData(@"%APPDATA%\x.exe", PathResolution.Unresolved, null)]
    [InlineData(@"%SystemRoot%%TEMP%\x.exe", PathResolution.Unresolved, null)]
    [InlineData(@"\Windows\x.exe", PathResolution.Unresolved, null)]
    [InlineData(@"C:x.exe", PathResolution.Unresolved, null)]
    [InlineData(@"tools\x.exe", PathResolution.Unresolved, null)]
    public void Paths_ResolveWithoutGuessing(string raw, PathResolution kind, string? expected)
    {
        var resolved = WindowsPath.Resolve(raw, Paths, null, _ => false);
        Assert.Equal(kind, resolved.Kind);
        Assert.Equal(expected, resolved.Path);
    }

    [Fact]
    public void BareNames_UseTheSearchOrder_AndSayWhereTheyWereFound()
    {
        var resolved = WindowsPath.Resolve("sc", Paths, null, p => p == @"C:\Windows\System32\sc.exe");
        Assert.Equal(PathResolution.SearchOrder, resolved.Kind);
        Assert.Equal(@"C:\Windows\System32\sc.exe", resolved.Path);

        var working = WindowsPath.Resolve("sc.exe", Paths, null, p => p == @"C:\Tools\sc.exe" || p == @"C:\Windows\System32\sc.exe", @"C:\Tools");
        Assert.Equal(@"C:\Tools\sc.exe", working.Path);

        Assert.Equal(PathResolution.Unresolved, WindowsPath.Resolve("nothere.exe", Paths, null, _ => false).Kind);
    }

    [Fact]
    public void TrailingDotsAndSpaces_AreRemovedLikeWindowsDoes_AndNoted()
    {
        var resolved = WindowsPath.Resolve(@"C:\Windows\System32\evil.exe. ", Paths, null, _ => false);
        Assert.Equal(@"C:\Windows\System32\evil.exe", resolved.Path);
        Assert.Contains("trailing", resolved.Note);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" /Embedding", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Windows\System32\app.exe -Embedding", @"C:\Windows\System32\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe")]
    [InlineData(@"app /x", "app")]
    public void LocalServerCommandLines_YieldTheProgram(string commandLine, string expected) =>
        Assert.Equal(expected, WindowsPath.ExtractCommandPath(commandLine));

    [Fact]
    public void TaskXml_WithADtd_IsRefused()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE Task [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]>
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Actions><Exec><Command>&x;</Command></Exec></Actions></Task>
            """;
        Assert.Throws<XmlException>(() => TaskFacts.FromXml(xml));
    }

    [Fact]
    public void TaskXml_UsesThePrincipalNamedByTheActionsContext()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals>
                <Principal id="Other"><UserId>S-1-5-18</UserId></Principal>
                <Principal id="Author"><GroupId>S-1-5-32-545</GroupId><RunLevel>HighestAvailable</RunLevel><LogonType>InteractiveToken</LogonType></Principal>
              </Principals>
              <Settings><Enabled>false</Enabled></Settings>
              <Actions Context="Author"><Exec><Command>a.exe</Command></Exec><SendEmail /></Actions>
            </Task>
            """;
        var facts = TaskFacts.FromXml(xml);
        Assert.Null(facts.UserId);
        Assert.Equal("S-1-5-32-545", facts.GroupId);
        Assert.Equal("Highest", facts.RunLevel);
        Assert.Equal("Interactive", facts.LogonType);
        Assert.False(facts.Enabled);
        Assert.Equal(new[] { TaskActionKinds.Exec, TaskActionKinds.SendEmail }, facts.Actions.Select(a => a.Kind));
    }

    [Fact]
    public void LoggedProjection_AcceptsSingleObjectsWherePowerShellDropsTheArray()
    {
        const string json = """{"Enabled":true,"UserId":"SYSTEM","RunLevel":"Highest","Actions":{"Execute":"a.exe","Arguments":"/x"},"Triggers":{"StartBoundary":"2026-01-01T00:00:00"}}""";
        var facts = TaskFacts.FromProjection(json);
        Assert.Equal("a.exe", Assert.Single(facts.Actions).Execute);
        Assert.Equal("2026-01-01T00:00:00", Assert.Single(facts.TriggerStarts));
    }

    [Fact]
    public void CertificateSubjects_AreParsed_IncludingQuotedValues()
    {
        const string subject = "CN=\"Vendor, Inc.\", O=\"Vendor, Inc.\", L=Somewhere, C=US";
        Assert.Equal("Vendor, Inc.", CertificateNames.CommonName(subject));
        Assert.Equal("Vendor, Inc.", CertificateNames.Organization(subject));
        Assert.Equal("Microsoft Windows", CertificateNames.CommonName(TaskScenarios.WindowsSubject));
        Assert.Null(CertificateNames.CommonName(null));
    }
}
