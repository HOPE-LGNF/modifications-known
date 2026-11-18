using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

/// <summary>
/// Ticket: difficulty must be matched on typed values, not on "whatever parses
/// as a number". An unknown key or a bool/string value means "自定义", never a
/// guessed preset.
/// </summary>
public sealed class SaveDifficultyPresetTests
{
    [Fact]
    public void Match_EveryPresetMatchesItsOwnTable()
    {
        foreach (var preset in SaveDifficultyPresets.All)
        {
            var match = SaveDifficultyPresets.Match(preset.EffectiveSettings);
            Assert.NotNull(match);
            Assert.Equal(preset.Key, match!.Key);
        }
    }

    [Fact]
    public void Table_UsesTheGamesRealKeys()
    {
        // Copied from RunSettings.settingTypes (51 keys in the shipped build).
        Assert.Equal(51, SaveDifficultyPresets.KnownKeyCount);
        Assert.Equal(51, SaveDifficultyPresets.NormalValues.Count);
        Assert.All(SaveDifficultyPresets.All, preset =>
            Assert.Equal(51, preset.EffectiveSettings.Count));
        Assert.True(SaveDifficultyPresets.NormalValues.ContainsKey("allowlayerendmovement"));
        Assert.Equal("艰难", SaveDifficultyPresets.Normal.Label);
    }

    [Fact]
    public void Match_ValueInsideTolerance_Matches()
    {
        var values = Copy(SaveDifficultyPresets.NormalValues);
        values["enemystrength"] = SaveSettingValue.FromNumber(1 + SaveDifficultyPresets.Tolerance - 0.001);

        Assert.Equal("normal", SaveDifficultyPresets.Match(values)?.Key);
    }

    [Fact]
    public void Match_ValueJustOutsideTolerance_IsCustom()
    {
        var values = Copy(SaveDifficultyPresets.NormalValues);
        values["enemystrength"] = SaveSettingValue.FromNumber(1 + SaveDifficultyPresets.Tolerance + 0.001);

        Assert.Null(SaveDifficultyPresets.Match(values));
    }

    [Fact]
    public void Match_WrongType_NeverMatches()
    {
        var asBoolean = Copy(SaveDifficultyPresets.NormalValues);
        asBoolean["timelimit"] = SaveSettingValue.FromBoolean(true);
        Assert.Null(SaveDifficultyPresets.Match(asBoolean));

        var asText = Copy(SaveDifficultyPresets.NormalValues);
        asText["timelimit"] = SaveSettingValue.FromText("60");
        Assert.Null(SaveDifficultyPresets.Match(asText));
    }

    [Fact]
    public void Match_UnknownKey_IsCustomInsteadOfAGuess()
    {
        var values = Copy(SaveDifficultyPresets.NormalValues);
        values["moddedSetting"] = SaveSettingValue.FromNumber(1);

        Assert.Null(SaveDifficultyPresets.Match(values));
    }

    [Fact]
    public void Match_MissingKeys_IsCustomNotAGuess()
    {
        // A save that only carries part of the table cannot be told apart from
        // another preset that happens to share those values.
        var values = Copy(SaveDifficultyPresets.NormalValues);
        values.Remove("oreamount");

        Assert.Null(SaveDifficultyPresets.Match(values));
    }

    [Fact]
    public void Match_OlderSaveWithoutAllowLayerEndMovement_IsCustom()
    {
        // Real saves (cVer 6-8) predate this key; the chosen rule is strict, so
        // they read as 自定义 until the game saves them again.
        var values = Copy(SaveDifficultyPresets.NormalValues);
        values.Remove("allowlayerendmovement");

        Assert.Null(SaveDifficultyPresets.Match(values));
    }

    [Fact]
    public void Match_SingleMatchingKey_IsNotEnough()
    {
        var values = new Dictionary<string, SaveSettingValue>(StringComparer.Ordinal)
        {
            ["lootmultiplier"] = SaveSettingValue.FromNumber(1)
        };

        Assert.Null(SaveDifficultyPresets.Match(values));
    }

    private static Dictionary<string, SaveSettingValue> Copy(
        IReadOnlyDictionary<string, SaveSettingValue> values) =>
        new(values, StringComparer.Ordinal);
}

public sealed class CappedReadStreamTests
{
    [Fact]
    public void Read_WithinLimit_ReturnsInnerContent()
    {
        using var inner = new MemoryStream([1, 2, 3, 4]);
        using var capped = new CappedReadStream(inner, 4, "测试");
        using var buffer = new MemoryStream();

        capped.CopyTo(buffer);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer.ToArray());
    }

    [Fact]
    public void Read_PastLimit_ThrowsAndStopsReading()
    {
        using var inner = new MemoryStream(new byte[64]);
        using var capped = new CappedReadStream(inner, 16, "测试数据");

        var error = Assert.Throws<StreamLimitExceededException>(() =>
        {
            var buffer = new byte[8];
            while (capped.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        });

        Assert.Contains("测试数据", error.Message);
        Assert.Contains("大小上限", error.Message);
    }
}
