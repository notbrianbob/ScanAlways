using System.Diagnostics;
using System.Text;

namespace ScanAlways.DefenderWatchdog;

/// <summary>Reads Defender's current state. Abstracted so the watchdog is testable without Defender.</summary>
public interface IDefenderStatusProvider
{
    Task<DefenderStatus> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads Defender's state with the documented <c>Get-MpComputerStatus</c>
/// cmdlet from the Defender PowerShell module. It works for a standard
/// (non-admin) user and is strictly read-only: the script only selects
/// properties and never calls <c>Set-MpPreference</c> or anything like it.
/// </summary>
public sealed class PowerShellDefenderStatusProvider : IDefenderStatusProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The query, exposed so tests and reviewers can see exactly what runs.</summary>
    public const string Script = """
        $ErrorActionPreference = 'Stop'
        $s = Get-MpComputerStatus
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
        } | ConvertTo-Json -Compress
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
