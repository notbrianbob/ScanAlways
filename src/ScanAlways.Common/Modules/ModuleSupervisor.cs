using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ScanAlways.Common.Modules;

/// <summary>
/// Runs every enabled module side by side inside one process and keeps them
/// isolated: a module that throws is logged and (optionally) restarted with
/// back-off, and never takes the hub or the other modules down with it.
/// </summary>
public sealed class ModuleSupervisor : BackgroundService
{
    private readonly IReadOnlyList<IScanAlwaysModule> _modules;
    private readonly IServiceProvider _services;
    private readonly HubOptions _options;
    private readonly ILogger<ModuleSupervisor> _logger;

    public ModuleSupervisor(
        EnabledModules modules,
        IServiceProvider services,
        IOptions<HubOptions> options,
        ILogger<ModuleSupervisor> logger)
    {
        _modules = modules.Modules;
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAllAsync(stoppingToken);

    /// <summary>Runs all enabled modules until cancelled or until each one finishes.</summary>
    public async Task RunAllAsync(CancellationToken cancellationToken)
    {
        if (_modules.Count == 0)
        {
            _logger.LogWarning(
                "No modules are enabled. Turn one on with {Section}:<Id>:Enabled = true.",
                ModuleCatalog.SectionName);
            return;
        }

        _logger.LogInformation(
            "ScanAlways protection service online. Running {Count} module(s): {Modules}",
            _modules.Count, string.Join(", ", _modules.Select(m => m.Id)));

        await Task.WhenAll(_modules.Select(m => SuperviseAsync(m, cancellationToken)));
    }

    private async Task SuperviseAsync(IScanAlwaysModule module, CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                _logger.LogInformation("Starting module {Id} ({Name}).", module.Id, module.DisplayName);

                // Task.Run so a module that blocks synchronously before its
                // first await cannot stall the others.
                await Task.Run(() => module.RunAsync(_services, cancellationToken), cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Module {Id} finished.", module.Id);
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A run that stayed healthy for longer than the max back-off
                // starts the back-off over.
                if (DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(_options.MaxRestartDelaySeconds))
                {
                    consecutiveFailures = 0;
                }
                consecutiveFailures++;

                if (!_options.RestartFailedModules)
                {
                    _logger.LogError(ex, "Module {Id} failed and will not be restarted.", module.Id);
                    return;
                }

                var delay = _options.RestartDelay(consecutiveFailures);
                _logger.LogError(ex, "Module {Id} failed; restarting in {Delay}.", module.Id, delay);

                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
