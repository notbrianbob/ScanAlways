using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace WatchLattice.Persistence.Verification.Windows;

/// <summary>Resolves accounts with the local security authority and profile folders from the ProfileList key.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAccountResolver : IAccountResolver
{
    private readonly SystemPaths _paths;

    public WindowsAccountResolver(SystemPaths paths) => _paths = paths;

    public string CurrentAccount
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
    }

    public string? ToSid(string account)
    {
        if (Accounts.IsSid(account)) return account;
        try
        {
            return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException) { return null; }
    }

    public string? ProfilePath(string sid)
    {
        if (!Accounts.IsSid(sid)) return null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid, writable: false);
            if (key?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw) return null;
            var resolved = WindowsPath.Resolve(raw, _paths, null, _ => false, defaultExtension: "");
            return resolved.Kind == PathResolution.Resolved ? resolved.Path : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
