using System.Text.Json.Nodes;
using WatchLattice.Common.Modules;
using WatchLattice.Common.Settings;
using Xunit;

namespace WatchLattice.Common.Tests;

public sealed class SettingsValidatorTests
{
    private static readonly ModuleCatalog Catalog = new(new IWatchLatticeModule[] { new SettingsModule() });
    private static readonly IReadOnlyList<SettingsSection> Sections = SettingsSchema.Build(Catalog);

    private const string Current = """
        {
          // Comments are allowed, as in any appsettings.json.
          "Modules": { "Sample": { "Enabled": true } },
          "Alerts": { "FeedPath": "C:\\ProgramData\\WatchLattice\\Alerts\\alerts.json", "HistoryLimit": 200 },
          "Sample": { "Count": 3, "Mode": "Loud", "Folders": [ "a" ] },
          "Logging": { "LogLevel": { "Default": "Information" } }
        }
        """;

    private static (JsonObject Current, JsonObject Proposed) Pair(Action<JsonObject> edit)
    {
        var current = SettingValues.Parse(Current);
        var proposed = SettingValues.Clone(current);
        edit(proposed);
        return (current, proposed);
    }

    [Fact]
    public void ValidEdits_PassAndAreListedAsChanges()
    {
        var (current, proposed) = Pair(p =>
        {
            SettingValues.Set(p, "modules:sample:enabled", JsonValue.Create(false));
            SettingValues.Set(p, "Sample:Mode", JsonValue.Create("Quiet"));
            SettingValues.Set(p, "Sample:Folders", new JsonArray("a", "b"));
            SettingValues.Set(p, "Sample:OptionalPath", null);
        });

        var check = SettingsValidator.Check(Sections, current, proposed);

        Assert.True(check.IsValid, string.Join("; ", check.Errors));
        Assert.Empty(check.Warnings);
        // A JSON null and a missing key mean the same thing to the hub, so setting OptionalPath to null is no change.
        Assert.Equal(new[] { "Modules:Sample:Enabled", "Sample:Folders", "Sample:Mode" },
            check.Changes.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
        // The key keeps the spelling already in the file.
        Assert.NotNull(proposed["Modules"]!["Sample"]!["Enabled"]);
    }

    [Theory]
    [InlineData("Sample:Count", "\"many\"")]
    [InlineData("Sample:Count", "-1")]
    [InlineData("Sample:Count", "1.5")]
    [InlineData("Sample:Ratio", "-0.5")]
    [InlineData("Sample:Flag", "\"maybe\"")]
    [InlineData("Sample:Mode", "\"Deafening\"")]
    [InlineData("Sample:Folders", "\"not a list\"")]
    [InlineData("Sample:Folders", "[ 1, 2 ]")]
    [InlineData("Sample:Name", "null")]
    [InlineData("Modules:Sample:Enabled", "\"yes\"")]
    public void WrongTypes_AreErrors(string path, string json)
    {
        var (current, proposed) = Pair(p => SettingValues.Set(p, path, JsonNode.Parse(json)));

        var check = SettingsValidator.Check(Sections, current, proposed);

        Assert.Contains(check.Errors, e => e.StartsWith(path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LockedSettings_CannotChange_ButMayStayAsTheyAre()
    {
        var (current, moved) = Pair(p => SettingValues.Set(p, "Alerts:FeedPath", JsonValue.Create("C:\\Users\\Public\\feed.json")));
        Assert.Contains(SettingsValidator.Check(Sections, current, moved).Errors, e => e.StartsWith("Alerts:FeedPath"));

        var (_, removed) = Pair(p => SettingValues.Remove(p, "Alerts:FeedPath"));
        Assert.False(SettingsValidator.Check(Sections, current, removed).IsValid);

        var (_, untouched) = Pair(p => SettingValues.Set(p, "Alerts:HistoryLimit", JsonValue.Create(300)));
        Assert.True(SettingsValidator.Check(Sections, current, untouched).IsValid);
    }

    [Fact]
    public void UnknownKeys_InKnownSections_AreWarnings_OtherSectionsAreLeftAlone()
    {
        var (current, proposed) = Pair(p =>
        {
            SettingValues.Set(p, "Sample:Cuont", JsonValue.Create(4));
            SettingValues.Set(p, "Modules:Retired:Enabled", JsonValue.Create(true));
            SettingValues.Set(p, "Logging:LogLevel:Default", JsonValue.Create("Debug"));
        });

        var check = SettingsValidator.Check(Sections, current, proposed);

        Assert.True(check.IsValid);
        Assert.Contains("Sample:Cuont is not used by this version.", check.Warnings);
        Assert.Contains("Modules:Retired:Enabled is not used by this version.", check.Warnings);
        Assert.DoesNotContain(check.Warnings, w => w.StartsWith("Logging"));
    }

    [Fact]
    public void ASectionReplacedByAValue_IsAnError()
    {
        var (current, proposed) = Pair(p => p["Sample"] = 5);
        Assert.Contains(SettingsValidator.Check(Sections, current, proposed).Errors, e => e.StartsWith("Sample must be a section"));
    }

    [Fact]
    public void Binding_CatchesValuesThatDoNotFitTheProperty()
    {
        var (_, proposed) = Pair(p => SettingValues.Set(p, "Sample:Count", JsonValue.Create(5_000_000_000)));

        Assert.True(SettingsValidator.Check(Sections, SettingValues.Parse(Current), proposed).IsValid);
        Assert.Single(SettingsValidator.CheckBinding(SettingsSchema.BindableSections(Catalog), proposed));
        Assert.Empty(SettingsValidator.CheckBinding(SettingsSchema.BindableSections(Catalog), SettingValues.Parse(Current)));
    }

    [Fact]
    public void Diff_IgnoresFormattingAndKeyCase()
    {
        var before = SettingValues.Parse("{ \"A\": { \"B\": 1, \"C\": [ \"x\" ] } }");
        var after = SettingValues.Parse("{\"a\":{\"c\":[\"x\"],\"b\":1}}");
        Assert.Empty(SettingsValidator.Diff(before, after));
        after["a"]!["b"] = 2;
        Assert.Equal("A:B: 1 -> 2", Assert.Single(SettingsValidator.Diff(before, after)).ToString());
    }
}
