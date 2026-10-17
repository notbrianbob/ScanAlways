using System.Security.Cryptography;
using WatchLattice.Persistence.Verification;
using Xunit;

namespace WatchLattice.Persistence.Tests.Verification;

public sealed class FileInspectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "watchlattice-inspect-" + Guid.NewGuid().ToString("N"));

    public FileInspectorTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ExistingFile_IsHashedAndSizedWithoutBeingChanged()
    {
        var path = Path.Combine(_directory, "target.bin");
        var bytes = RandomNumberGenerator.GetBytes(100_000);
        File.WriteAllBytes(path, bytes);
        var written = File.GetLastWriteTimeUtc(path);

        var evidence = await new FileInspector(new UnavailableAuthenticodeInspector()).InspectAsync(path, default);

        Assert.True(evidence.Exists);
        Assert.Equal(bytes.Length, evidence.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), evidence.Sha256);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(SignatureState.Unknown, evidence.Signature!.State);
    }

    [Fact]
    public async Task MissingFilesAndFolders_AreReportedAsSuch()
    {
        var inspector = new FileInspector(new UnavailableAuthenticodeInspector());
        var missing = await inspector.InspectAsync(Path.Combine(_directory, "nope.dll"), default);
        Assert.False(missing.Exists);
        Assert.Null(missing.Error);

        var folder = await inspector.InspectAsync(_directory, default);
        Assert.False(folder.Exists);
        Assert.NotNull(folder.Error);
    }
}
