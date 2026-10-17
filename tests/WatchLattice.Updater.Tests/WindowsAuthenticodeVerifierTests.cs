using System.Runtime.InteropServices;
using Xunit;

namespace WatchLattice.Updater.Tests;

/// <summary>
/// Live checks of WinVerifyTrust on Windows CI runners, against the .NET
/// runtime's own Microsoft-signed coreclr.dll; elsewhere there is nothing to call.
/// </summary>
public class WindowsAuthenticodeVerifierTests
{
    private static string SignedRuntimeFile => Path.Join(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");

    [Fact]
    public void Verify_ReportsAMicrosoftSignedFileAsValid()
    {
        if (!OperatingSystem.IsWindows()) return;

        var signature = new WindowsAuthenticodeVerifier().Verify(SignedRuntimeFile);

        Assert.True(signature.Status == SignatureStatus.Valid, signature.Problem);
        Assert.Contains("Microsoft", signature.Publisher);
    }

    [Fact]
    public void Verify_ReportsUnsignedFilesAsNotSigned()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var verifier = new WindowsAuthenticodeVerifier();

        // This test assembly is a PE file that nobody signed.
        Assert.Equal(SignatureStatus.NotSigned, verifier.Verify(typeof(WindowsAuthenticodeVerifierTests).Assembly.Location).Status);
        Assert.Equal(SignatureStatus.NotSigned, verifier.Verify(temp.Write("notes.txt", "not a program")).Status);
    }

    [Fact]
    public void Verify_ReportsAModifiedSignedFileAsInvalid()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var bytes = File.ReadAllBytes(SignedRuntimeFile);
        bytes[bytes.Length / 2] ^= 0xFF;
        var tampered = temp.Combine("coreclr.dll");
        File.WriteAllBytes(tampered, bytes);

        var signature = new WindowsAuthenticodeVerifier().Verify(tampered);

        Assert.Equal(SignatureStatus.Invalid, signature.Status);
        Assert.NotNull(signature.Problem);
    }
}
