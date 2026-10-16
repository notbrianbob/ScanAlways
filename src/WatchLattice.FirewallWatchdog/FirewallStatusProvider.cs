using System.Diagnostics;
using System.Text;

namespace WatchLattice.FirewallWatchdog;

/// <summary>Reads Windows Firewall's current state. Abstracted so the watchdog is testable without Windows.</summary>
public interface IFirewallStatusProvider
{
    Task<FirewallStatus> GetStatusAsync(bool includeInboundAllowRules, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the effective firewall policy with the documented NetSecurity cmdlets
/// (<c>Get-NetFirewallProfile</c>, <c>Get-NetConnectionProfile</c> and,
/// when rule monitoring is on, <c>Get-NetFirewallRule</c>). It is strictly
/// read-only: the script only selects properties and never calls
/// <c>Set-NetFirewall*</c>, <c>New-NetFirewall*</c> or anything like them.
/// </summary>
public sealed class PowerShellFirewallStatusProvider : IFirewallStatusProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>The query, exposed so tests and reviewers can see exactly what runs.</summary>
    public const string Script = """
        $ErrorActionPreference = 'Stop'
        $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore | ForEach-Object {
            [pscustomobject]@{
                Name                 = [string]$_.Name
                Enabled              = [string]$_.Enabled
                DefaultInboundAction = [string]$_.DefaultInboundAction
            }
        })
        $active = $null
        try {
            $active = @(Get-NetConnectionProfile | ForEach-Object { [string]$_.NetworkCategory })
        } catch {
            # No connected network means no active profile; any other failure leaves it unknown.
            if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { $active = @() }
        }
        $rules = $null
        $rulesError = $null
        if ($IncludeRules) {
            try {
                $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Direction Inbound -Action Allow -Enabled True |
                    ForEach-Object {
                        [pscustomobject]@{
                            Id          = [string]$_.Name
                            DisplayName = [string]$_.DisplayName
                            Profile     = [string]$_.Profile
                        }
                    })
            } catch {
                if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { $rules = @() }
                else { $rulesError = $_.Exception.Message }
            }
        }
        [pscustomobject]@{
            Profiles         = $profiles
            ActiveCategories = $active
            Rules            = $rules
            RulesError       = $rulesError
        } | ConvertTo-Json -Compress -Depth 4
        """;

    /// <summary>The full command for one check: a fixed switch followed by <see cref="Script"/>.</summary>
    public static string BuildScript(bool includeInboundAllowRules) =>
        (includeInboundAllowRules ? "$IncludeRules = $true" : "$IncludeRules = $false") + "\n" + Script;

    public async Task<FirewallStatus> GetStatusAsync(bool includeInboundAllowRules, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return FirewallStatus.Unavailable("Windows Firewall status is only available on Windows.");
        }

        // Use the in-box Windows PowerShell by full path, so a lookalike
        // powershell.exe earlier on PATH can't answer for the firewall.
        var powershell = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        var startInfo = new ProcessStartInfo(powershell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-NoProfile", "-NonInteractive",
            "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildScript(includeInboundAllowRules))),
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("powershell.exe did not start.");

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited on its own between the timeout and the kill; nothing left to stop.
                }
                throw;
            }

            var output = await stdout;
            if (process.ExitCode != 0)
            {
                var error = (await stderr).Trim();
                return FirewallStatus.Unavailable(
                    $"The firewall query failed (exit {process.ExitCode}): {(error.Length > 0 ? error : "no details")}");
            }

            return FirewallStatusParser.Parse(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FirewallStatus.Unavailable("The firewall query timed out.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return FirewallStatus.Unavailable($"Could not run PowerShell: {ex.Message}");
        }
    }
}
