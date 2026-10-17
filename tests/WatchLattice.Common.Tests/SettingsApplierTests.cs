using System.Text.Json.Nodes;
using WatchLattice.Common.Modules;
using WatchLattice.Common.Settings;
using Xunit;

namespace WatchLattice.Common.Tests;

public sealed class SettingsApplierTests : IDisposable
{
    private const string Original = "{ \"Modules\": { \"Sample\": { \"Enabled\": true } }, \"Sample\": { \"Count\": 3 } }";
    private static readonly ModuleCatalog Catalog = new(new IWatchLatticeModule[] { new SettingsModule() });
    private readonly string _hub = Path.Combine(Path.GetTempPath(), "watchlattice-settings-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();
    private readonly FakeService _service = new();

    public SettingsApplierTests()
    {
        Directory.CreateDirectory(_hub);
        File.WriteAllText(SettingsPath, Original);
    }

    private string SettingsPath => Path.Combine(_hub, SettingsApplier.SettingsFileName);
    private string PendingPath => Path.Combine(_hub, SettingsApplier.PendingFileName);
    private string Backups => Path.Combine(_hub, SettingsApplier.BackupFolderName);

    private SettingsApplier Applier(IServiceControl? service, DateTimeOffset? now = null) =>
        new(SettingsSchema.Build(Catalog), SettingsSchema.BindableSections(Catalog), service, _log.Add, TimeSpan.FromMilliseconds(200),
            now is null ? null : () => now.Value);

    private void Stage(Action<JsonObject> edit)
    {
        var settings = SettingValues.Parse(Original);
        edit(settings);
        File.WriteAllText(PendingPath, SettingValues.Serialize(settings));
    }

    [Fact]
    public async Task ValidChanges_AreSaved_BackedUp_AndTheServiceRestarted()
    {
        _service.Running = true;
        Stage(s => SettingValues.Set(s, "Sample:Count", JsonValue.Create(9)));

        var outcome = await Applier(_service).ApplyAsync(_hub);

        Assert.Equal(SettingsOutcome.Applied, outcome);
        Assert.Equal(9, SettingValues.Get(SettingValues.Parse(File.ReadAllText(SettingsPath)), "Sample:Count")!.GetValue<int>());
        Assert.Equal(Original, File.ReadAllText(Assert.Single(Directory.GetFiles(Backups))));
        Assert.Equal(new[] { "stop", "start" }, _service.Calls);
        Assert.False(File.Exists(PendingPath));
        Assert.Contains(_log, l => l.Contains("Sample:Count: 3 -> 9"));
    }

    [Fact]
    public async Task InvalidChanges_LeaveEverythingAsItWas()
    {
        _service.Running = true;
        Stage(s => SettingValues.Set(s, "Sample:Count", JsonValue.Create("lots")));

        Assert.Equal(SettingsOutcome.Invalid, await Applier(_service).ApplyAsync(_hub));

        Assert.Equal(Original, File.ReadAllText(SettingsPath));
        Assert.False(Directory.Exists(Backups));
        Assert.Empty(_service.Calls);
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task UnreadableJson_IsInvalid()
    {
        File.WriteAllText(PendingPath, "{ \"Sample\": ");
        Assert.Equal(SettingsOutcome.Invalid, await Applier(_service).ApplyAsync(_hub));
        Assert.Equal(Original, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task NothingStaged_OrNothingChanged_DoesNotTouchTheService()
    {
        _service.Running = true;
        Assert.Equal(SettingsOutcome.Failed, await Applier(_service).ApplyAsync(_hub));
        Stage(_ => { });
        Assert.Equal(SettingsOutcome.NoChanges, await Applier(_service).ApplyAsync(_hub));
        Assert.Empty(_service.Calls);
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public async Task AHubThatStopsAfterRestarting_GetsThePreviousSettingsBack()
    {
        _service.Running = true;
        _service.StopsAfterStart = 1;
        Stage(s => SettingValues.Set(s, "Modules:Sample:Enabled", JsonValue.Create(false)));

        Assert.Equal(SettingsOutcome.RolledBack, await Applier(_service).ApplyAsync(_hub));

        Assert.Equal(Original, File.ReadAllText(SettingsPath));
        Assert.Equal(new[] { "stop", "start", "start" }, _service.Calls);
        Assert.True(_service.Running);
        Assert.Single(Directory.GetFiles(Backups));
    }

    [Fact]
    public async Task WithoutARunningService_TheFileIsSavedForTheNextStart()
    {
        Stage(s => SettingValues.Set(s, "Sample:Count", JsonValue.Create(4)));
        Assert.Equal(SettingsOutcome.Applied, await Applier(_service).ApplyAsync(_hub));
        Assert.Empty(_service.Calls);

        Stage(s => SettingValues.Set(s, "Sample:Count", JsonValue.Create(5)));
        Assert.Equal(SettingsOutcome.Applied, await Applier(null).ApplyAsync(_hub));
        Assert.Equal(5, SettingValues.Get(SettingValues.Parse(File.ReadAllText(SettingsPath)), "Sample:Count")!.GetValue<int>());
        Assert.Contains(_log, l => l.Contains("next time the hub starts"));
    }

    [Fact]
    public async Task OnlyTheNewestBackupsAreKept()
    {
        var time = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < SettingsApplier.BackupsKept + 3; i++)
        {
            Stage(s => SettingValues.Set(s, "Sample:Count", JsonValue.Create(100 + i)));
            Assert.Equal(SettingsOutcome.Applied, await Applier(null, time.AddMinutes(i)).ApplyAsync(_hub));
        }

        var kept = Directory.GetFiles(Backups).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(SettingsApplier.BackupsKept, kept.Length);
        Assert.Equal("appsettings-20261017-000300-000.json", kept[0]);
    }

    public void Dispose() => Directory.Delete(_hub, recursive: true);

    private sealed class FakeService : IServiceControl
    {
        public bool Running { get; set; }
        public int StopsAfterStart { get; set; }
        public List<string> Calls { get; } = new();

        public bool IsRunning() => Running;

        public void Stop(TimeSpan timeout)
        {
            Calls.Add("stop");
            Running = false;
        }

        public void Start(TimeSpan timeout)
        {
            Calls.Add("start");
            // Simulates a hub that starts, then exits because it cannot use its settings.
            Running = StopsAfterStart-- <= 0;
        }
    }
}
