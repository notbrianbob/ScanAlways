using System.Text.Json.Nodes;
using WatchLattice.Common.Settings;
using Xunit;

namespace WatchLattice.Common.Tests;

public sealed class SettingValuesTests
{
    private static SettingField Field(SettingKind kind, bool nullable = false, params string[] choices) =>
        new("Section:Value", "Value", kind, nullable, choices.Length == 0 ? null : choices);

    [Fact]
    public void GetSetRemove_MatchKeysCaseInsensitively_AndCreateSections()
    {
        var root = SettingValues.Parse("{ \"Canary\": { \"Folders\": [] } }");

        SettingValues.Set(root, "canary:MarkHidden", JsonValue.Create(false));
        SettingValues.Set(root, "Decoy:Intensity", JsonValue.Create("Idle"));

        Assert.False(root["Canary"]!["MarkHidden"]!.GetValue<bool>());
        Assert.Equal("Idle", SettingValues.Get(root, "DECOY:intensity")!.GetValue<string>());
        Assert.True(SettingValues.TryGet(root, "Canary:Folders", out _));
        Assert.False(SettingValues.TryGet(root, "Canary:Missing", out _));

        SettingValues.Remove(root, "Canary:markhidden");
        Assert.False(SettingValues.TryGet(root, "Canary:MarkHidden", out _));
    }

    [Fact]
    public void Effective_FallsBackToTheDefaultOnlyWhenMissing()
    {
        var field = new SettingField("A:B", "B", SettingKind.Integer, true, Default: JsonValue.Create(7));
        Assert.Equal(7, SettingValues.Effective(SettingValues.Parse("{}"), field)!.GetValue<int>());
        Assert.Null(SettingValues.Effective(SettingValues.Parse("{ \"A\": { \"B\": null } }"), field));
    }

    [Theory]
    [InlineData(SettingKind.Boolean, false, "true", "true")]
    [InlineData(SettingKind.Boolean, false, " False ", "false")]
    [InlineData(SettingKind.Integer, false, "300", "300")]
    [InlineData(SettingKind.Integer, true, "", null)]
    [InlineData(SettingKind.Number, false, "2.5", "2.5")]
    [InlineData(SettingKind.Text, true, "  ", null)]
    [InlineData(SettingKind.Text, false, " C:\\Logs ", "\"C:\\\\Logs\"")]
    [InlineData(SettingKind.TextList, false, "C:\\One\r\n\r\n  C:\\Two  \n", "[\"C:\\\\One\",\"C:\\\\Two\"]")]
    [InlineData(SettingKind.TextList, false, "", "[]")]
    public void TryParse_TurnsTextIntoJson(SettingKind kind, bool nullable, string text, string? json)
    {
        Assert.True(SettingValues.TryParse(Field(kind, nullable), text, out var value, out var error), error);
        Assert.Equal(json, value?.ToJsonString());
    }

    [Theory]
    [InlineData(SettingKind.Boolean, "yes")]
    [InlineData(SettingKind.Integer, "-5")]
    [InlineData(SettingKind.Integer, "1.5")]
    [InlineData(SettingKind.Integer, "")]
    [InlineData(SettingKind.Number, "NaN")]
    [InlineData(SettingKind.Text, "")]
    [InlineData(SettingKind.Unsupported, "00:01:00")]
    public void TryParse_RejectsBadInput(SettingKind kind, string text)
    {
        Assert.False(SettingValues.TryParse(Field(kind), text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Choices_MatchIgnoringCase_AndUseTheCanonicalName()
    {
        var field = Field(SettingKind.Choice, false, "Idle", "Balanced", "FullScan");
        Assert.True(SettingValues.TryParse(field, "fullscan", out var value, out _));
        Assert.Equal("\"FullScan\"", value!.ToJsonString());
        Assert.False(SettingValues.TryParse(field, "Turbo", out _, out _));
    }

    [Fact]
    public void Format_ShowsListsOnePerLine()
    {
        var field = Field(SettingKind.TextList);
        Assert.Equal("a" + Environment.NewLine + "b", SettingValues.Format(field, new JsonArray("a", "b")));
        Assert.Equal("", SettingValues.Format(Field(SettingKind.Text, true), null));
        Assert.Equal("false", SettingValues.Format(Field(SettingKind.Boolean), JsonNode.Parse("false")));
        Assert.Equal("60", SettingValues.Format(Field(SettingKind.Integer), JsonNode.Parse("60")));
    }

    [Fact]
    public void Parse_RejectsAnythingButAnObject()
    {
        Assert.Throws<InvalidDataException>(() => SettingValues.Parse("[1, 2]"));
        Assert.NotNull(SettingValues.Parse("\uFEFF{ \"A\": 1 }")["A"]);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => SettingValues.Parse("{ \"open\": "));
    }
}
