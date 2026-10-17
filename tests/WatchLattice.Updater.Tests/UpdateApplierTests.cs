using Xunit;

namespace WatchLattice.Updater.Tests;

public class UpdateApplierTests
{
    private static readonly ReleaseVersion Current = ReleaseVersion.Parse("0.1.0");
    private const string PackageName = "WatchLattice-0.2.0-win-x64.zip";

    private sealed class Setup : IDisposable
    {
        public TempDirectory Temp { get; } = new();
        public FakeHttpHandler Http { get; } = new();
        public FakeReleaseSource Source { get; } = new();
        public FakeServiceControl Service { get; } = new() { Running = true };
        public List<string> Log { get; } = new();
        public FakeSignatureVerifier? Signatures { get; set; }
        public byte[] Package { get; }

        public Setup(byte[]? package = null)
        {
            Package = package ?? Releases.Package("v0.2.0");
            Source.Next = () => Releases.Make("0.2.0");
            Http.Add(Releases.DownloadUrl("0.2.0", PackageName), Package);
            Http.Add(Releases.DownloadUrl("0.2.0", "SHA256SUMS.txt"), $"{Releases.Sha256(Package)}  {PackageName}\n");
            Temp.Write("Hub/ScanAlways.Protection.Service.exe", "hub old");
            Temp.Write("Hub/appsettings.json", "my settings");
            Temp.Write("Tray/ScanAlways.Tray.exe", "tray old");
        }

        public string HubDir => Temp.Combine("Hub");
        public string TrayDir => Temp.Combine("Tray");

        public Task<UpdateOutcome> RunAsync(ReleaseVersion? current = null, string? tray = "") =>
            new UpdateApplier(Source, new HttpClient(Http), new UpdaterOptions(), Service, Log.Add, Signatures)
                .ApplyAsync(current ?? Current, HubDir, tray == "" ? TrayDir : tray, default);

        public string Read(params string[] parts) => File.ReadAllText(Temp.Combine(parts));

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public async Task Apply_VerifiesInstallsAndRestartsTheService()
    {
        using var setup = new Setup();

        var outcome = await setup.RunAsync();

        Assert.Equal(UpdateOutcome.Installed, outcome);
        Assert.Equal("hub v0.2.0", setup.Read("Hub", "ScanAlways.Protection.Service.exe"));
        Assert.Equal("tray v0.2.0", setup.Read("Tray", "ScanAlways.Tray.exe"));
        Assert.Equal("my settings", setup.Read("Hub", "appsettings.json"));
        Assert.Equal(new[] { "stop", "start" }, setup.Service.Calls);
        Assert.Contains(setup.Log, line => line.StartsWith("SHA-256 verified"));
        Assert.Empty(Directory.GetDirectories(setup.HubDir, UpdateApplier.StagingPrefix + "*"));
    }

    [Fact]
    public async Task Apply_ChangesNothing_WhenTheChecksumDoesNotMatch()
    {
        using var setup = new Setup();
        setup.Http.Add(Releases.DownloadUrl("0.2.0", "SHA256SUMS.txt"), $"{new string('a', 64)}  {PackageName}\n");

        var outcome = await setup.RunAsync();

        Assert.Equal(UpdateOutcome.VerificationFailed, outcome);
        Assert.Equal("hub old", setup.Read("Hub", "ScanAlways.Protection.Service.exe"));
        Assert.Equal("tray old", setup.Read("Tray", "ScanAlways.Tray.exe"));
        Assert.Empty(setup.Service.Calls);
        Assert.Contains(setup.Log, line => line.Contains("SHA-256 mismatch"));
        Assert.Empty(Directory.GetDirectories(setup.HubDir, UpdateApplier.StagingPrefix + "*"));
    }

    [Fact]
    public async Task Apply_ChangesNothing_WhenTheChecksumFileHasNoEntryForThePackage()
    {
        using var setup = new Setup();
        setup.Http.Add(Releases.DownloadUrl("0.2.0", "SHA256SUMS.txt"), $"{Releases.Sha256(setup.Package)}  something-else.zip\n");

        Assert.Equal(UpdateOutcome.VerificationFailed, await setup.RunAsync());
        Assert.Equal("hub old", setup.Read("Hub", "ScanAlways.Protection.Service.exe"));
    }

    [Fact]
    public async Task Apply_RefusesADownloadRedirectedOffGitHub()
    {
        using var setup = new Setup();
        setup.Http.Add(Releases.DownloadUrl("0.2.0", PackageName), setup.Package, finalUrl: "https://downloads.example.com/x.zip");

        Assert.Equal(UpdateOutcome.VerificationFailed, await setup.RunAsync());
        Assert.Equal("hub old", setup.Read("Hub", "ScanAlways.Protection.Service.exe"));
        Assert.Empty(setup.Service.Calls);
    }

    [Fact]
    public async Task Apply_RefusesAPackageLargerThanAllowed()
    {
        using var setup = new Setup();

        var outcome = await new UpdateApplier(setup.Source, new HttpClient(setup.Http), new UpdaterOptions { MaxPackageBytes = 10 },
            setup.Service, setup.Log.Add).ApplyAsync(Current, setup.HubDir, setup.TrayDir, default);

        Assert.Equal(UpdateOutcome.VerificationFailed, outcome);
        Assert.Equal("hub old", setup.Read("Hub", "ScanAlways.Protection.Service.exe"));
    }

    [Fact]
    public async Task Apply_UpdatesOnlyTheHub_WhenNoTrayIsInstalledAlongside()
    {
        using var setup = new Setup();

        Assert.Equal(UpdateOutcome.Installed, await setup.RunAsync(tray: null));
        Assert.Equal("hub v0.2.0", setup.Read("Hub", "ScanAlways.Protection.Service.exe"));
        Assert.Equal("tray old", setup.Read("Tray", "ScanAlways.Tray.exe"));
    }

    [Fact]
    public async Task Apply_ReportsUpToDate_NotSupported_AndNotAvailable()
    {
        using var setup = new Setup();
        Assert.Equal(UpdateOutcome.UpToDate, await setup.RunAsync(ReleaseVersion.Parse("0.2.0")));
        Assert.Equal(UpdateOutcome.NotSupported, await setup.RunAsync(ReleaseVersion.Development));

        setup.Source.Next = () => Releases.Make("0.2.0", checksums: false);
        Assert.Equal(UpdateOutcome.NotAvailable, await setup.RunAsync());

        setup.Source.Next = () => null;
        Assert.Equal(UpdateOutcome.UpToDate, await setup.RunAsync());
        Assert.Empty(setup.Service.Calls);
        Assert.Equal("hub old", setup.Read("Hub", "ScanAlways.Protection.Service.exe"));
    }

    [Fact]
    public async Task Apply_DoesNotStartAServiceThatWasNotRunning()
    {
        using var setup = new Setup();
        setup.Service.Running = false;

        Assert.Equal(UpdateOutcome.Installed, await setup.RunAsync());
        Assert.Empty(setup.Service.Calls);
    }

    private static readonly FileSignature Ours = new(SignatureStatus.Valid, "CN=WatchLattice (SignPath Foundation)");
    private static readonly FileSignature Microsoft = new(SignatureStatus.Valid, "CN=Microsoft Corporation");

    [Fact]
    public async Task Apply_InstallsAnUnsignedRelease_OverAnUnsignedInstall()
    {
        using var setup = new Setup { Signatures = new FakeSignatureVerifier() };

        Assert.Equal(UpdateOutcome.Installed, await setup.RunAsync());
        Assert.Contains(setup.Log, line => line.Contains("not code-signed"));
        Assert.Contains("WatchLattice.Common.dll", setup.Signatures.Checked);
        Assert.DoesNotContain("appsettings.json", setup.Signatures.Checked);
    }

    [Fact]
    public async Task Apply_InstallsASignedRelease_OverAnUnsignedInstall()
    {
        using var setup = new Setup { Signatures = new FakeSignatureVerifier { Staged = _ => Ours } };

        Assert.Equal(UpdateOutcome.Installed, await setup.RunAsync());
        Assert.Contains(setup.Log, line => line.Contains("code-signed by " + Ours.Publisher));
    }

    [Fact]
    public async Task Apply_InstallsARelease_SignedByTheSamePublisherAsTheInstall()
    {
        using var setup = new Setup(Releases.Package("v0.2.0", new Dictionary<string, string> { ["Hub/Microsoft.Extensions.Hosting.dll"] = "ms" }))
        {
            Signatures = new FakeSignatureVerifier
            {
                Installed = Ours,
                Staged = name => name.StartsWith("Microsoft.") ? Microsoft : Ours,
            },
        };

        Assert.Equal(UpdateOutcome.Installed, await setup.RunAsync());
        Assert.Equal("hub v0.2.0", setup.Read("Hub", "ScanAlways.Protection.Service.exe"));
    }

    [Fact]
    public async Task Apply_RefusesAnUnsignedRelease_OverASignedInstall()
    {
        using var setup = new Setup { Signatures = new FakeSignatureVerifier { Installed = Ours } };

        Assert.Equal(UpdateOutcome.VerificationFailed, await setup.RunAsync());
        AssertNothingChanged(setup);
        Assert.Contains(setup.Log, line => line.Contains("is not signed, but the installed WatchLattice is signed by"));
    }

    [Fact]
    public async Task Apply_RefusesAnUnsignedFile_InAnOtherwiseSignedRelease()
    {
        using var setup = new Setup
        {
            Signatures = new FakeSignatureVerifier
            {
                Installed = Ours,
                Staged = name => name == "ScanAlways.Tray.exe" ? FileSignature.NotSigned : Ours,
            },
        };

        Assert.Equal(UpdateOutcome.VerificationFailed, await setup.RunAsync());
        AssertNothingChanged(setup);
    }

    [Fact]
    public async Task Apply_RefusesARelease_SignedByADifferentPublisher()
    {
        using var setup = new Setup
        {
            Signatures = new FakeSignatureVerifier
            {
                Installed = Ours,
                Staged = _ => new FileSignature(SignatureStatus.Valid, "CN=Someone Else"),
            },
        };

        Assert.Equal(UpdateOutcome.VerificationFailed, await setup.RunAsync());
        AssertNothingChanged(setup);
        Assert.Contains(setup.Log, line => line.Contains("CN=Someone Else"));
    }

    [Fact]
    public async Task Apply_RefusesAnInvalidSignature_EvenOverAnUnsignedInstall()
    {
        using var setup = new Setup
        {
            Signatures = new FakeSignatureVerifier
            {
                Staged = name => name == "WatchLattice.Common.dll"
                    ? new FileSignature(SignatureStatus.Invalid, Problem: "The digital signature of the object did not verify.")
                    : Ours,
            },
        };

        Assert.Equal(UpdateOutcome.VerificationFailed, await setup.RunAsync());
        AssertNothingChanged(setup);
        Assert.Contains(setup.Log, line => line.Contains("WatchLattice.Common.dll has an invalid signature"));
    }

    [Fact]
    public async Task Apply_TreatsAnInvalidInstalledSignature_AsUnsigned()
    {
        using var setup = new Setup
        {
            Signatures = new FakeSignatureVerifier { Installed = new FileSignature(SignatureStatus.Invalid, Problem: "revoked") },
        };

        Assert.Equal(UpdateOutcome.Installed, await setup.RunAsync());
        Assert.Contains(setup.Log, line => line.StartsWith("Warning: the installed"));
    }

    private static void AssertNothingChanged(Setup setup)
    {
        Assert.Equal("hub old", setup.Read("Hub", "ScanAlways.Protection.Service.exe"));
        Assert.Equal("tray old", setup.Read("Tray", "ScanAlways.Tray.exe"));
        Assert.Empty(setup.Service.Calls);
        Assert.Empty(Directory.GetDirectories(setup.HubDir, UpdateApplier.StagingPrefix + "*"));
    }
}
