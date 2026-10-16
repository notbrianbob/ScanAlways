using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScanAlways.Canary;

/// <summary>
/// Hosts the <see cref="CanaryWatcher"/> as a long-running background service,
/// from a console window or as a Windows service.
/// </summary>
public sealed class CanaryWorker : BackgroundService
{
    private readonly CanaryWatcher _watcher;
    private readonly ILogger<CanaryWorker> _logger;

    public CanaryWorker(CanaryWatcher watcher, ILogger<CanaryWorker> logger)
    {
        _watcher = watcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScanAlways canary watcher online.");
        await _watcher.RunAsync(stoppingToken);
    }
}
