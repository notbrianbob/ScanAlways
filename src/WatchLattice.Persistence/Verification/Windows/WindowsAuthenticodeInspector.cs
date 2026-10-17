using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace WatchLattice.Persistence.Verification.Windows;

/// <summary>
/// Checks a file's Authenticode signature with WinVerifyTrust, without
/// loading the file. Most Windows system files carry no embedded signature;
/// they are signed through a catalog in the system catalog database, so a
/// file without an embedded signature is looked up there by its hash. The
/// signer certificate is read from the verified signature itself, whichever
/// kind it is. Revocation is checked against cached revocation data only, so
/// a verification never waits on the network; when that data is missing the
/// result says revocation was not checked.
///
/// The updater has its own, embedded-only verifier for release packages
/// (WatchLattice.Updater.WindowsAuthenticodeVerifier); this one is separate so
/// the persistence module does not depend on the updater.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAuthenticodeInspector : IAuthenticodeInspector
{
    private const uint TrustENoSignature = 0x800B0100;
    private const uint TrustESubjectFormUnknown = 0x800B0003;
    private const uint TrustEProviderUnknown = 0x800B0001;
    private const uint CryptERevocationOffline = 0x80092013;
    private const uint CryptENoRevocationCheck = 0x80092012;
    private const uint CertERevocationFailure = 0x800B010E;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public SignatureEvidence Inspect(string path)
    {
        var embedded = Verify(path, catalog: null, out var lastError);
        if (embedded.Status == 0 || !IsNoSignature(embedded.Status, lastError))
            return Evidence(embedded, SignatureKind.Embedded, path, null);

        var catalog = FindCatalog(path, out var problem);
        if (catalog is null)
            return problem is null ? SignatureEvidence.NotSigned : new(SignatureState.Unknown, SignatureKind.None, Problem: problem);
        try
        {
            var result = Verify(path, catalog, out _);
            return Evidence(result, SignatureKind.Catalog, path, catalog.CatalogFile);
        }
        finally
        {
            catalog.Dispose();
        }
    }

    private static bool IsNoSignature(int result, int lastError) =>
        (uint)result is TrustESubjectFormUnknown or TrustEProviderUnknown
        || (uint)result == TrustENoSignature && (uint)lastError is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown;

    private static SignatureEvidence Evidence(VerifyResult result, SignatureKind kind, string path, string? catalogFile)
    {
        var where = catalogFile is null ? null : $"catalog {Path.GetFileName(catalogFile)}";
        if (result.Status == 0)
        {
            return result.Signer is null
                ? new(SignatureState.Unknown, kind, Problem: "The signature verified, but its signer certificate could not be read.")
                : new(SignatureState.Valid, kind, result.Signer.Subject, result.Signer.Issuer, result.Signer.Thumbprint,
                    RevocationChecked: result.RevocationChecked, Problem: where);
        }
        return new(SignatureState.Invalid, kind, result.Signer?.Subject, result.Signer?.Issuer, result.Signer?.Thumbprint,
            Problem: $"{new Win32Exception(result.Status).Message} (0x{result.Status:X8}){(where is null ? "" : ", " + where)}");
    }

    private sealed record VerifyResult(int Status, X509Certificate2? Signer, bool RevocationChecked);

    /// <summary>Verifies once with cached revocation data; if that data is missing, again without revocation.</summary>
    private static VerifyResult Verify(string path, CatalogMatch? catalog, out int lastError)
    {
        var first = WinVerifyTrustOnce(path, catalog, checkRevocation: true, out lastError);
        if ((uint)first.Status is CryptERevocationOffline or CryptENoRevocationCheck or CertERevocationFailure)
        {
            var second = WinVerifyTrustOnce(path, catalog, checkRevocation: false, out lastError);
            first.Signer?.Dispose();
            return second with { RevocationChecked = false };
        }
        return first;
    }

    private static VerifyResult WinVerifyTrustOnce(string path, CatalogMatch? catalog, bool checkRevocation, out int lastError)
    {
        var filePath = Marshal.StringToHGlobalUni(path);
        var memberTag = catalog is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(catalog.MemberTag);
        var catalogPath = catalog is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(catalog.CatalogFile);
        var hash = catalog is null ? IntPtr.Zero : Marshal.AllocHGlobal(catalog.Hash.Length);
        IntPtr choice;
        uint unionChoice;
        if (catalog is null)
        {
            var info = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = filePath };
            choice = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(info, choice, false);
            unionChoice = 1;   // WTD_CHOICE_FILE
        }
        else
        {
            Marshal.Copy(catalog.Hash, 0, hash, catalog.Hash.Length);
            var info = new WinTrustCatalogInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustCatalogInfo>(),
                pcwszCatalogFilePath = catalogPath,
                pcwszMemberTag = memberTag,
                pcwszMemberFilePath = filePath,
                pbCalculatedFileHash = hash,
                cbCalculatedFileHash = (uint)catalog.Hash.Length,
                hCatAdmin = catalog.Admin,
            };
            choice = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustCatalogInfo>());
            Marshal.StructureToPtr(info, choice, false);
            unionChoice = 2;   // WTD_CHOICE_CATALOG
        }
        try
        {
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,                                         // WTD_UI_NONE
                fdwRevocationChecks = checkRevocation ? 1u : 0u,         // WTD_REVOKE_WHOLECHAIN : WTD_REVOKE_NONE
                dwUnionChoice = unionChoice,
                pChoice = choice,
                dwStateAction = 1,                                      // WTD_STATEACTION_VERIFY
                // WTD_CACHE_ONLY_URL_RETRIEVAL: never fetch revocation data over the network.
                dwProvFlags = 0x1000 | (checkRevocation ? 0x80u : 0u),  // + WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            var action = GenericVerifyV2;
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            lastError = Marshal.GetLastWin32Error();
            X509Certificate2? signer = null;
            try { signer = SignerOf(data.hWVTStateData); }
            catch (CryptographicException) { }
            data.dwStateAction = 2;                                     // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return new(status, signer, checkRevocation);
        }
        finally
        {
            Marshal.FreeHGlobal(choice);
            Marshal.FreeHGlobal(filePath);
            if (memberTag != IntPtr.Zero) Marshal.FreeHGlobal(memberTag);
            if (catalogPath != IntPtr.Zero) Marshal.FreeHGlobal(catalogPath);
            if (hash != IntPtr.Zero) Marshal.FreeHGlobal(hash);
        }
    }

    /// <summary>The leaf certificate of the first signer, from WinVerifyTrust's state data.</summary>
    private static X509Certificate2? SignerOf(IntPtr state)
    {
        if (state == IntPtr.Zero) return null;
        var provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero) return null;
        var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero) return null;
        var certificate = WTHelperGetProvCertFromChain(signer, 0);
        if (certificate == IntPtr.Zero) return null;
        // CRYPT_PROVIDER_CERT { DWORD cbStruct; PCCERT_CONTEXT pCert; ... }: pCert sits at pointer alignment.
        var context = Marshal.ReadIntPtr(certificate, IntPtr.Size);
        return context == IntPtr.Zero ? null : new X509Certificate2(context);
    }

    private sealed class CatalogMatch : IDisposable
    {
        public required IntPtr Admin { get; init; }
        public required IntPtr Info { get; init; }
        public required string CatalogFile { get; init; }
        public required byte[] Hash { get; init; }
        public string MemberTag => Convert.ToHexString(Hash);

        public void Dispose()
        {
            if (Info != IntPtr.Zero) CryptCATAdminReleaseCatalogContext(Admin, Info, 0);
            CryptCATAdminReleaseContext(Admin, 0);
        }
    }

    /// <summary>Finds a system catalog that lists the file's hash (SHA-256 catalogs first, then SHA-1).</summary>
    private static CatalogMatch? FindCatalog(string path, out string? problem)
    {
        problem = null;
        foreach (var algorithm in new[] { "SHA256", null })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0))
            {
                problem = "The system catalog database could not be opened: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                continue;
            }
            var keep = false;
            try
            {
                byte[] hash;
                using (var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    uint size = 0;
                    CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0);
                    if (size == 0 || size > 64) continue;
                    hash = new byte[size];
                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash, 0)) continue;
                }
                var info = CryptCATAdminEnumCatalogFromHash(admin, hash, (uint)hash.Length, 0, IntPtr.Zero);
                if (info == IntPtr.Zero) continue;
                var catalogInfo = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>() };
                if (!CryptCATCatalogInfoFromContext(info, ref catalogInfo, 0))
                {
                    CryptCATAdminReleaseCatalogContext(admin, info, 0);
                    continue;
                }
                keep = true;
                problem = null;
                return new CatalogMatch { Admin = admin, Info = info, CatalogFile = catalogInfo.wszCatalogFile, Hash = hash };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = "The file could not be hashed for the catalog lookup: " + ex.Message;
                return null;
            }
            finally
            {
                if (!keep) CryptCATAdminReleaseContext(admin, 0);
            }
        }
        return null;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem,
        [MarshalAs(UnmanagedType.LPWStr)] string? pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, SafeFileHandle hFile, ref uint pcbHash, byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CatalogInfo psCatInfo, uint dwFlags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustCatalogInfo
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pChoice;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
