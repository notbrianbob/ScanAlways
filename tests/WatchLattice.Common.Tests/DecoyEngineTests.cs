using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WatchLattice.Common;
using Xunit;

namespace WatchLattice.Common.Tests;

public class DecoyEngineTests
{
    [Fact]
    public async Task Run_ReadsFilesWithoutModifyingThem()
    {
        var dir = Directory.CreateTempSubdirectory("watchlattice-test");
        try
        {
            var filePath = Path.Combine(dir.FullName, "sample.bin");
            var contents = new byte[4096];
            Random.Shared.NextBytes(contents);
            await File.WriteAllBytesAsync(filePath, contents);
            var before = File.GetLastWriteTimeUtc(filePath);

            var options = Options.Create(new DecoyOptions
            {
                ScanRoots = { dir.FullName },
                Intensity = DecoyIntensity.FullScan,
                MaxFilesPerSecond = 100,
                WriteScanLog = false,
                RandomSeed = 7,
            });

            var engine = new DecoyEngine(options, NullLogger<DecoyEngine>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await engine.RunAsync(cts.Token);

            Assert.True(engine.Statistics.FilesTouched >= 1);
            Assert.True(engine.Statistics.BytesRead >= contents.Length);

            // The decoy is read-only: file content and timestamp are untouched.
            Assert.Equal(contents, await File.ReadAllBytesAsync(filePath));
            Assert.Equal(before, File.GetLastWriteTimeUtc(filePath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Run_WithNoExistingRoots_ReturnsQuietly()
    {
        var options = Options.Create(new DecoyOptions
        {
            ScanRoots = { Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()) },
            WriteScanLog = false,
        });

        var engine = new DecoyEngine(options, NullLogger<DecoyEngine>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await engine.RunAsync(cts.Token);

        Assert.Equal(0, engine.Statistics.FilesTouched);
    }

    [Fact]
    public async Task Run_WithEmptyRoot_WaitsBetweenSweepsInsteadOfSpinning()
    {
        var dir = Directory.CreateTempSubdirectory("watchlattice-empty");
        try
        {
            var (sweeps, wait) = await RunUntilSweepWaitAsync(dir.FullName);

            Assert.Equal(1, sweeps);
            Assert.True(wait >= TimeSpan.FromSeconds(59), $"Waited only {wait} before the next sweep.");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Run_WithEmptyRoot_AndNoSweepFloor_StillWaitsASecond()
    {
        var dir = Directory.CreateTempSubdirectory("watchlattice-empty");
        try
        {
            var (sweeps, wait) = await RunUntilSweepWaitAsync(dir.FullName, minSweepIntervalSeconds: 0);

            Assert.Equal(1, sweeps);
            Assert.True(wait >= TimeSpan.FromSeconds(0.9), $"Waited only {wait} before the next sweep.");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Run_WithTinyRoot_WaitsBetweenSweepsInsteadOfRestartingAtOnce()
    {
        var dir = Directory.CreateTempSubdirectory("watchlattice-tiny");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "only.txt"), "hello");

            var (sweeps, wait) = await RunUntilSweepWaitAsync(dir.FullName);

            Assert.Equal(1, sweeps);
            Assert.True(wait >= TimeSpan.FromSeconds(59), $"Waited only {wait} before the next sweep.");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Runs the engine until it asks to wait half a second or more (the gap between
    /// sweeps), then stops it. If it never waits, it is stopped after a few
    /// sweeps so the test fails instead of hanging.
    /// </summary>
    private static async Task<(long Sweeps, TimeSpan Wait)> RunUntilSweepWaitAsync(string root, int minSweepIntervalSeconds = 60)
    {
        var options = Options.Create(new DecoyOptions
        {
            ScanRoots = { root },
            Intensity = DecoyIntensity.FullScan,
            MaxFilesPerSecond = 100,
            MinSweepIntervalSeconds = minSweepIntervalSeconds,
            WriteScanLog = false,
            RandomSeed = 7,
        });
        var time = new LongWaitTimeProvider();
        var engine = new DecoyEngine(options, NullLogger<DecoyEngine>.Instance, time);

        using var cts = new CancellationTokenSource();
        var sweeps = 0;
        engine.StatusChanged += line =>
        {
            if (line.StartsWith("Sweep complete", StringComparison.Ordinal) && ++sweeps >= 5)
            {
                cts.Cancel();
            }
        };

        var run = Task.Run(() => engine.RunAsync(cts.Token));
        await Task.WhenAny(time.LongWaitRequested, run).WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        return (engine.Statistics.CyclesCompleted, time.LongWaitRequested.IsCompleted ? time.LongWaitRequested.Result : TimeSpan.Zero);
    }

    /// <summary>
    /// Real clock, but timers shorter than half a second fire straight away and longer
    /// ones never fire: the first long wait is reported and the engine parks there.
    /// </summary>
    private sealed class LongWaitTimeProvider : TimeProvider
    {
        private readonly TaskCompletionSource<TimeSpan> _longWait = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TimeSpan> LongWaitRequested => _longWait.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.FromMilliseconds(500))
            {
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
            else
            {
                _longWait.TrySetResult(dueTime);
            }
            return new ParkedTimer();
        }

        private sealed class ParkedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
