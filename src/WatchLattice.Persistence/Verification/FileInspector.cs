using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;

namespace WatchLattice.Persistence.Verification;

/// <summary>
/// Hashes a file and reads its version resource and signature. Opens it for
/// reading only, shared with other readers and writers, and never loads or
/// runs it. Files larger than <see cref="MaxHashBytes"/> are not hashed.
/// </summary>
public sealed class FileInspector : IFileInspector
{
    public const long MaxHashBytes = 512L * 1024 * 1024;

    private readonly IAuthenticodeInspector _signatures;

    public FileInspector(IAuthenticodeInspector signatures) => _signatures = signatures;

    public bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return false; }
    }

    public async Task<FileEvidence> InspectAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return Directory.Exists(path)
                    ? new FileEvidence(path, false, Error: "The path is a folder, not a file.")
                    : new FileEvidence(path, false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return new FileEvidence(path, false, Error: ex.Message);
        }

        var evidence = new FileEvidence(path, true, info.Length,
            CreatedUtc: new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            LastWriteUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            IsReparsePoint: info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        try
        {
            if (info.Length > MaxHashBytes)
            {
                evidence = evidence with { Error = $"Larger than {MaxHashBytes / (1024 * 1024)} MB, so it was not hashed." };
            }
            else
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                evidence = evidence with { Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return evidence with { Error = ex.Message };
        }

        try
        {
            // Reads the version resource as data; the file is not loaded as a module.
            var version = FileVersionInfo.GetVersionInfo(path);
            evidence = evidence with
            {
                FileVersion = Empty(version.FileVersion),
                ProductVersion = Empty(version.ProductVersion),
                CompanyName = Empty(version.CompanyName),
                ProductName = Empty(version.ProductName),
                OriginalFilename = Empty(version.OriginalFilename),
                FileDescription = Empty(version.FileDescription),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { }

        SignatureEvidence signature;
        try { signature = _signatures.Inspect(path); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            signature = new SignatureEvidence(SignatureState.Unknown, SignatureKind.None, Problem: ex.Message);
        }
        return evidence with { Signature = signature };
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Used where Authenticode is not available (off Windows): every signature is unknown, never valid.</summary>
public sealed class UnavailableAuthenticodeInspector : IAuthenticodeInspector
{
    public SignatureEvidence Inspect(string path) =>
        new(SignatureState.Unknown, SignatureKind.None, Problem: "Signature checks are only available on Windows.");
}
