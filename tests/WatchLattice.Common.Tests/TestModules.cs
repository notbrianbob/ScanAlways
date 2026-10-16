using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WatchLattice.Common.Modules;

namespace WatchLattice.Common.Tests;

/// <summary>A configurable fake module for catalog and supervisor tests.</summary>
public class FakeModule : IWatchLatticeModule
{
    public FakeModule() : this("Fake")
    {
    }

    public FakeModule(string id, bool enabledByDefault = true, Func<CancellationToken, Task>? run = null)
    {
        Id = id;
        EnabledByDefault = enabledByDefault;
        Run = run ?? (ct => Task.Delay(Timeout.Infinite, ct));
    }

    public string Id { get; }
    public string DisplayName => $"{Id} module";
    public string Description => "Test double.";
    public bool EnabledByDefault { get; }
    public Func<CancellationToken, Task> Run { get; }

    public int ConfigureCalls { get; private set; }
    public int RunCalls;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => ConfigureCalls++;

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCalls);
        return Run(cancellationToken);
    }
}

/// <summary>Has no parameterless constructor, so discovery must skip it.</summary>
public sealed class NeedsArgumentsModule : FakeModule
{
    public NeedsArgumentsModule(string id) : base(id)
    {
    }
}
