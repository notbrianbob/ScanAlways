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
}
