using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScanAlways.Common;

namespace ScanAlways.Decoy;

/// <summary>
/// Hosts the <see cref="DecoyEngine"/> as a long-running background service.
/// Works identically whether the process runs from a console window or as a
/// Windows service.
/// </summary>
public sealed class DecoyWorker : BackgroundService
{
    private readonly DecoyEngine _engine;
    private readonly ILogger<DecoyWorker> _logger;

    public DecoyWorker(DecoyEngine engine, ILogger<DecoyWorker> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScanAlways protection service online.");
        await _engine.RunAsync(stoppingToken);
    }
}
