using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScanAlways.Common.Modules;
using Xunit;

namespace ScanAlways.Common.Tests;

public class ModuleSupervisorTests
{
    private static ModuleSupervisor Supervisor(HubOptions options, params IScanAlwaysModule[] modules) =>
        new(
            new EnabledModules(modules),
            new ServiceCollection().BuildServiceProvider(),
            Options.Create(options),
            NullLogger<ModuleSupervisor>.Instance);

    private static readonly HubOptions FastRestarts = new()
    {
        RestartFailedModules = true,
        RestartDelaySeconds = 0.01,
        MaxRestartDelaySeconds = 0.05,
    };

    [Fact]
    public async Task RunAll_RunsEveryModule_UntilCancelled()
    {
        var a = new FakeModule("A");
        var b = new FakeModule("B");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Supervisor(FastRestarts, a, b).RunAllAsync(cts.Token);

        Assert.Equal(1, a.RunCalls);
        Assert.Equal(1, b.RunCalls);
    }

    [Fact]
    public async Task RunAll_AFailingModule_DoesNotStopTheOthers_AndIsRestarted()
    {
        var healthy = new FakeModule("Healthy");
        var flaky = new FakeModule("Flaky", run: _ => throw new InvalidOperationException("boom"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        await Supervisor(FastRestarts, healthy, flaky).RunAllAsync(cts.Token);

        Assert.Equal(1, healthy.RunCalls);
        Assert.True(flaky.RunCalls >= 2, $"expected restarts, got {flaky.RunCalls} run(s)");
    }

    [Fact]
    public async Task RunAll_WithRestartsDisabled_RunsAFailingModuleOnce()
    {
        var flaky = new FakeModule("Flaky", run: _ => throw new InvalidOperationException("boom"));
        var options = new HubOptions { RestartFailedModules = false };

        await Supervisor(options, flaky).RunAllAsync(CancellationToken.None);

        Assert.Equal(1, flaky.RunCalls);
    }

    [Fact]
    public async Task RunAll_AModuleThatFinishes_IsNotRestarted()
    {
        var oneShot = new FakeModule("OneShot", run: _ => Task.CompletedTask);

        await Supervisor(FastRestarts, oneShot).RunAllAsync(CancellationToken.None);

        Assert.Equal(1, oneShot.RunCalls);
    }

    [Fact]
    public async Task RunAll_WithNoModules_ReturnsImmediately()
    {
        await Supervisor(FastRestarts).RunAllAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(10, 600)]
    public void RestartDelay_DoublesUpToTheCap(int failures, double expectedSeconds)
    {
        var options = new HubOptions { RestartDelaySeconds = 30, MaxRestartDelaySeconds = 600 };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.RestartDelay(failures));
    }
}
