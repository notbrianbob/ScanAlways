using System.Diagnostics;
using System.Text;

namespace WatchLattice.DefenderWatchdog;

/// <summary>Reads Defender's current state. Abstracted so the watchdog is testable without Defender.</summary>
public interface IDefenderStatusProvider
{
    Task<DefenderStatus> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads Defender's state with the documented <c>Get-MpComputerStatus</c> and
/// <c>Get-MpPreference</c> cmdlets from the Defender PowerShell module. Both
/// work for a standard user, though Defender hides the exclusion lists unless
/// the caller is elevated (the hub service is). It is strictly read-only: the
/// script only selects properties and never calls <c>Set-MpPreference</c> or
/// anything like it.
/// </summary>
public sealed class PowerShellDefenderStatusProvider : IDefenderStatusProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The query, exposed so tests and reviewers can see exactly what runs.</summary>
    public const string Script = """
        $ErrorActionPreference = 'Stop'
        $s = Get-MpComputerStatus
        $p = $null; $pe = $null
        try { $p = Get-MpPreference } catch { $pe = $_.Exception.Message }
        [pscustomobject]@{
            AMServiceEnabled              = $s.AMServiceEnabled
            AntivirusEnabled              = $s.AntivirusEnabled
            RealTimeProtectionEnabled     = $s.RealTimeProtectionEnabled
            BehaviorMonitorEnabled        = $s.BehaviorMonitorEnabled
            IoavProtectionEnabled         = $s.IoavProtectionEnabled
            OnAccessProtectionEnabled     = $s.OnAccessProtectionEnabled
            IsTamperProtected             = $s.IsTamperProtected
            AMRunningMode                 = [string]$s.AMRunningMode
            AntivirusSignatureVersion     = [string]$s.AntivirusSignatureVersion
            AntivirusSignatureLastUpdated = if ($s.AntivirusSignatureLastUpdated) { $s.AntivirusSignatureLastUpdated.ToUniversalTime().ToString('o') } else { $null }
            PreferencesError              = $pe
            Preferences                   = if ($p) { [pscustomobject]@{
                ExclusionPath           = @($p.ExclusionPath | Where-Object { $_ } | ForEach-Object { [string]$_ })
                ExclusionExtension      = @($p.ExclusionExtension | Where-Object { $_ } | ForEach-Object { [string]$_ })
                ExclusionProcess        = @($p.ExclusionProcess | Where-Object { $_ } | ForEach-Object { [string]$_ })
                ExclusionIpAddress      = @($p.ExclusionIpAddress | Where-Object { $_ } | ForEach-Object { [string]$_ })
                AsrRuleIds              = @($p.AttackSurfaceReductionRules_Ids | Where-Object { $_ } | ForEach-Object { [string]$_ })
                AsrRuleActions          = @($p.AttackSurfaceReductionRules_Actions | Where-Object { $null -ne $_ } | ForEach-Object { [int]$_ })
                MAPSReporting           = if ($null -ne $p.MAPSReporting) { [int]$p.MAPSReporting } else { $null }
                DisableBlockAtFirstSeen = $p.DisableBlockAtFirstSeen
                CloudBlockLevel         = if ($null -ne $p.CloudBlockLevel) { [int]$p.CloudBlockLevel } else { $null }
            } } else { $null }
        } | ConvertTo-Json -Compress -Depth 4
        """;

    public async Task<DefenderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DefenderStatus.Unavailable("Microsoft Defender status is only available on Windows.");
        }

        // Use the in-box Windows PowerShell by full path, so a lookalike
        // powershell.exe earlier on PATH can't answer for Defender.
        var powershell = Path.Combine(
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
            "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)),
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
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            var output = await stdout;
            if (process.ExitCode != 0)
            {
                var error = (await stderr).Trim();
                return DefenderStatus.Unavailable(
                    $"Get-MpComputerStatus failed (exit {process.ExitCode}): {(error.Length > 0 ? error : "no details")}");
            }

            return DefenderStatusParser.Parse(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DefenderStatus.Unavailable("Get-MpComputerStatus timed out.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return DefenderStatus.Unavailable($"Could not run PowerShell: {ex.Message}");
        }
    }
}
