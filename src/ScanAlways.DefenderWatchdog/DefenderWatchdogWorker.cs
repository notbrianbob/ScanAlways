using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScanAlways.DefenderWatchdog;

/// <summary>
/// Hosts the <see cref="DefenderWatchdog"/> as a long-running background
/// service, from a console window or as a Windows service.
/// </summary>
public sealed class DefenderWatchdogWorker : BackgroundService
{
    private readonly DefenderWatchdog _watchdog;
    private readonly ILogger<DefenderWatchdogWorker> _logger;

    public DefenderWatchdogWorker(DefenderWatchdog watchdog, ILogger<DefenderWatchdogWorker> logger)
    {
        _watchdog = watchdog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScanAlways Defender watchdog online.");
        await _watchdog.RunAsync(stoppingToken);
    }
}
