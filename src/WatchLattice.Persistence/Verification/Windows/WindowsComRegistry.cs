using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace WatchLattice.Persistence.Verification.Windows;

/// <summary>
/// Reads CLSID keys from the registry, read-only. Machine-wide classes come
/// from HKLM\SOFTWARE\Classes in the requested view; per-user classes from
/// the user's loaded hives (HKU\&lt;SID&gt;_Classes, which is HKCU\Software\Classes
/// for that user, and HKU\&lt;SID&gt;\Software\Classes). Values are read without
/// expanding environment variables, so paths are resolved by the verifier
/// with machine-wide variables only. Key paths are built from a validated
/// CLSID and SID, never from other input.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsComRegistry : IComRegistry
{
    public ComRegistration Read(ComScope scope, string clsid, string? userSid = null)
    {
        if (!WindowsPath.TryNormalizeClsid(clsid, out var canonical) || canonical != clsid)
            return new($"CLSID {clsid}", false, Error: "Not a canonical CLSID.");
        if (scope == ComScope.User)
        {
            if (!Accounts.IsSid(userSid)) return new($"per-user {clsid}", false, Error: "Not a valid SID.");
            var locations = new[] { userSid + @"_Classes\CLSID\" + clsid, userSid + @"\Software\Classes\CLSID\" + clsid };
            var hiveLoaded = false;
            foreach (var path in locations)
            {
                var result = ReadKey(RegistryHive.Users, RegistryView.Default, path, @"HKU\" + path, out var rootFound);
                hiveLoaded |= rootFound;
                if (result.Exists || !result.Readable) return result;
            }
            return new(@"HKU\" + locations[0], false, HiveUnavailable: !hiveLoaded);
        }
        var view = scope == ComScope.Machine64 ? RegistryView.Registry64 : RegistryView.Registry32;
        var key = @"SOFTWARE\Classes\CLSID\" + clsid;
        return ReadKey(RegistryHive.LocalMachine, view, key, $@"HKLM\{key} ({(scope == ComScope.Machine64 ? "64" : "32")}-bit view)", out _);
    }

    public IReadOnlyList<string> LoadedUserSids()
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        return users.GetSubKeyNames()
            .Where(n => Accounts.IsSid(n) && (n.StartsWith("S-1-5-21-", StringComparison.Ordinal) || n.StartsWith("S-1-12-1-", StringComparison.Ordinal)))
            .ToArray();
    }

    private static ComRegistration ReadKey(RegistryHive hive, RegistryView view, string path, string display, out bool rootFound)
    {
        rootFound = false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            if (hive == RegistryHive.Users)
            {
                using var userRoot = root.OpenSubKey(path[..path.IndexOf('\\')], writable: false);
                if (userRoot is null) return new(display, false);
            }
            rootFound = true;
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null) return new(display, false);
            return new(display, true, DefaultValue(key, "InprocServer32"), DefaultValue(key, "LocalServer32"), DefaultValue(key, "TreatAs"));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return new(display, false, AccessDenied: true, Error: "Access denied.");
        }
        catch (IOException ex)
        {
            return new(display, false, Error: ex.Message);
        }
    }

    private static string? DefaultValue(RegistryKey clsidKey, string subKey)
    {
        using var key = clsidKey.OpenSubKey(subKey, writable: false);
        var value = key?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
