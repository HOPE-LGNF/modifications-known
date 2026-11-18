using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

/// <summary>
/// Ticket: read whatever save layout a group happens to contain (QoL multi-slot,
/// vanilla single slot, or unknown files) without ever touching a real profile.
/// Every fixture is synthesised in a temp directory from gzip JSON payloads.
/// </summary>
public sealed class SaveSlotReaderTests
{
    private static readonly SaveSlotReader Reader = new();

    [Fact]
    public void ReadGroup_VanillaSingleSave_ReadsFieldsWithoutMetadata()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        var path = WriteSave(group, "save.sv", VanillaPayload(4, 3725.5, SaveDifficultyPresets.Normal.EffectiveSettings, 4242));
        WriteText(group, "settings.json", "{}");
        WriteText(group, "qol-mod-plugin-version-cache.json", "{}");
        WriteText(group, "player.log", "noise");

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.SingleSave, details.Layout);
        Assert.Null(details.Error);
        var save = Assert.Single(details.Saves);
        Assert.Equal(SaveArtifactKind.SingleSave, save.Kind);
        Assert.Equal("存档 1", save.Label);
        Assert.Null(save.SlotNumber);
        Assert.Equal("save.sv", save.FileName);
        Assert.Equal(new FileInfo(path).Length, save.FileSize);
        Assert.Equal(64, save.ContentHash.Length);
        Assert.Equal(save.ContentHash.ToLowerInvariant(), save.ContentHash);
        Assert.Equal(4, save.LayerIndex);
        Assert.Equal("第 5 层", save.LayerName);
        Assert.Equal(3725.5, save.IgtSeconds);
        Assert.Equal("1 小时 2 分", save.RunTimeLabel);
        Assert.Equal("normal", save.DifficultyKey);
        Assert.Equal("艰难", save.DifficultyLabel);
        Assert.Equal(4242, save.CharacterId);
        Assert.False(save.IsMercRun);
        Assert.False(save.Cheated);
        Assert.Null(save.SeedValue);
        Assert.Null(save.DepthMeters);
        Assert.False(save.HasMetadata);
        Assert.True(save.IsReadable);
        Assert.True(save.IsMostRecent);
        Assert.Null(save.Error);
        Assert.Equal(0, save.ModCount);
        Assert.False(save.IsModded);
        Assert.NotNull(save.SavedAt);
        Assert.InRange(save.SavedAt!.Value, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.False(string.IsNullOrWhiteSpace(save.SavedAtLabel));
        Assert.Equal(1, details.SlotCount);
        Assert.Equal(0, details.DeclaredSlotCount);
        Assert.Equal(0, details.EmptySlotCount);
        Assert.StartsWith("1 个存档（原版单槽） · 最近保存：存档 1", details.SummaryLabel);
        Assert.Equal("存档 1", details.MostRecentLabel);
    }

    [Fact]
    public void ReadGroup_QolMultiSlot_UsesMetadataSlotsAndEmptyDeclarations()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        var game = temp.CreateDirectory("game");
        WriteLang(game, new Dictionary<string, string>
        {
            ["/other/layertitle2"] = "测试二层",
            ["/other/layertitle3"] = "测试三层"
        });
        WriteText(group, "qol_slots.json", """{"Slots":[1,2,3,4],"Names":{"1":"一号"}}""");

        WriteSave(group, "slot_1.sv", VanillaPayload(1, 60, SaveDifficultyPresets.Relaxed.EffectiveSettings, 11));
        WriteText(group, "slot_1.sv.meta", Meta(new Dictionary<string, object?>
        {
            ["LocationName"] = "一层",
            ["Timestamp"] = "2026-01-01T00:00:00Z",
            ["BiomeIndex"] = 1,
            ["IGTSeconds"] = 60,
            ["SaveKind"] = "Manual",
            ["SeedValue"] = 111L,
            ["Mods"] = new[] { "mod-a" }
        }));

        WriteSave(group, "slot_3.sv", VanillaPayload(2, 7200, SaveDifficultyPresets.Normal.EffectiveSettings, 33));
        WriteText(group, "slot_3.sv.meta", Meta(new Dictionary<string, object?>
        {
            ["LocationName"] = "三层",
            ["Timestamp"] = "2026-02-01T12:00:00Z",
            ["BiomeIndex"] = 2,
            ["IGTSeconds"] = 7200,
            ["SaveKind"] = "Manual",
            ["SeedValue"] = 333L,
            ["Mods"] = Array.Empty<string>()
        }));
        File.Copy(Path.Combine(group, "slot_3.sv"), Path.Combine(group, "save.sv"));

        WriteSave(group, "autosave.sv", VanillaPayload(2, 500, SaveDifficultyPresets.Normal.EffectiveSettings, 33));
        WriteText(group, "autosave.sv.meta", Meta(new Dictionary<string, object?>
        {
            ["Timestamp"] = "2026-01-15T00:00:00Z",
            ["BiomeIndex"] = 2,
            ["IGTSeconds"] = 500
        }));

        var details = Reader.ReadGroup(group, game);

        Assert.Equal(SaveLayoutKind.MultiSlot, details.Layout);
        Assert.Null(details.Error);
        Assert.Equal(4, details.SlotCount);
        Assert.Equal(4, details.DeclaredSlotCount);
        Assert.Equal(2, details.EmptySlotCount);
        Assert.Equal(new[] { 2, 4 }, details.EmptySlots);
        Assert.Equal(3, details.Saves.Count);
        Assert.StartsWith("4 个槽位 · 1 个自动存档 · 最近保存：槽 3", details.SummaryLabel);
        Assert.Contains("三层", details.SummaryLabel);
        Assert.Contains("艰难", details.SummaryLabel);

        var first = Assert.Single(details.Saves, save => save.SlotNumber == 1);
        Assert.Equal(SaveArtifactKind.Slot, first.Kind);
        // 名称来自 qol_slots.json 的 Names 对象（槽 1 -> 一号）。
        Assert.Equal("槽 1 · 一号", first.Label);
        Assert.Equal("一号", first.CustomName);
        Assert.Equal("一层", first.LocationName);
        Assert.Equal("测试二层", first.LayerName);
        Assert.Equal("轻松", first.DifficultyLabel);
        Assert.Equal("relaxed", first.DifficultyKey);
        Assert.Equal(1, first.ModCount);
        Assert.True(first.IsModded);
        Assert.True(first.HasMetadata);
        Assert.Equal(111L, first.SeedValue);
        Assert.Equal("1 分", first.RunTimeLabel);
        Assert.False(first.IsMostRecent);

        var merged = Assert.Single(details.Saves, save => save.SlotNumber == 3);
        Assert.Equal("槽 3 · 当前", merged.Label);
        Assert.Equal(SaveArtifactKind.Slot, merged.Kind);
        Assert.Equal("测试三层", merged.LayerName);
        Assert.True(merged.IsReadable);
        Assert.True(merged.IsMostRecent);
        Assert.Equal(merged.Label, details.MostRecentLabel);

        var autosave = Assert.Single(details.Saves, save => save.Kind == SaveArtifactKind.Autosave);
        Assert.Equal("自动存档", autosave.Label);
        Assert.False(autosave.IsMostRecent);

        // The slot declaration file is never listed as a save.
        Assert.DoesNotContain(details.Saves, save => save.FileName == "qol_slots.json");
    }

    [Fact]
    public void ReadGroup_SaveAndSlotWithSameContent_MergeWithSlotWinning()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_5.sv", VanillaPayload(0, 30, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteText(group, "slot_5.sv.meta", Meta(new Dictionary<string, object?>
        {
            ["LocationName"] = "合并点",
            ["Timestamp"] = "2026-03-01T08:00:00Z",
            ["BiomeIndex"] = 0
        }));
        File.Copy(Path.Combine(group, "slot_5.sv"), Path.Combine(group, "save.sv"));

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.MultiSlot, details.Layout);
        var save = Assert.Single(details.Saves);
        Assert.Equal(SaveArtifactKind.Slot, save.Kind);
        Assert.Equal(5, save.SlotNumber);
        Assert.Equal("slot_5.sv", save.FileName);
        Assert.Equal("槽 5 · 当前", save.Label);
        Assert.Equal("合并点", save.LocationName);
        Assert.True(save.HasMetadata);
        Assert.Equal(5, details.SlotCount);
        Assert.Equal(0, details.DeclaredSlotCount);
        Assert.Equal(0, details.EmptySlotCount);
        Assert.Equal("槽 5 · 当前", details.MostRecentLabel);
    }

    [Fact]
    public void ReadGroup_UniqueSaveSv_BecomesCurrentProgress()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(1, 120, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteText(group, "slot_1.sv.meta", Meta(new Dictionary<string, object?>
        {
            ["Timestamp"] = "2026-01-01T00:00:00Z",
            ["BiomeIndex"] = 1
        }));
        WriteSave(group, "save.sv", VanillaPayload(1, 300, SaveDifficultyPresets.Normal.EffectiveSettings, 7));

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.MultiSlot, details.Layout);
        Assert.Equal(2, details.Saves.Count);
        var current = Assert.Single(details.Saves, save => save.Kind == SaveArtifactKind.Current);
        Assert.Equal("当前进度", current.Label);
        Assert.Equal("save.sv", current.FileName);
        Assert.Null(current.SlotNumber);
        Assert.True(current.IsMostRecent);
        Assert.Equal("当前进度", details.MostRecentLabel);
        Assert.Equal(7, current.CharacterId);
    }

    [Fact]
    public void ReadGroup_VanillaAutosave_IsListedWithoutBeingSwallowed()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "save.sv", VanillaPayload(3, 100, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteSave(group, "autosave.sv", VanillaPayload(3, 90, SaveDifficultyPresets.Normal.EffectiveSettings));

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.SingleSave, details.Layout);
        Assert.Equal(2, details.Saves.Count);
        var autosave = Assert.Single(details.Saves, save => save.Kind == SaveArtifactKind.Autosave);
        Assert.Equal("自动存档", autosave.Label);
        Assert.StartsWith("1 个存档（原版单槽） · 1 个自动存档", details.SummaryLabel);
        Assert.Equal("自动存档", details.MostRecentLabel);
    }

    [Fact]
    public void ReadGroup_AutosaveIdenticalToSave_MergesIntoTheSaveEntry()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "save.sv", VanillaPayload(3, 100, SaveDifficultyPresets.Normal.EffectiveSettings));
        File.Copy(Path.Combine(group, "save.sv"), Path.Combine(group, "autosave.sv"));

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.SingleSave, details.Layout);
        var save = Assert.Single(details.Saves);
        Assert.Equal(SaveArtifactKind.SingleSave, save.Kind);
        Assert.Equal("存档 1 · 自动存档", save.Label);
        Assert.True(save.IsMostRecent);
    }

    [Fact]
    public void ReadGroup_UnknownLayout_ReadsGenericFilesByName()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("custom");
        WriteSave(group, "custom_a.sv", VanillaPayload(2, 42, SaveDifficultyPresets.Relaxed.EffectiveSettings, 99));
        File.WriteAllBytes(Path.Combine(group, "custom_b.sav"), [0x00, 0x01, 0x02, 0x03]);
        WriteText(group, "notes.txt", "ignored");

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.Generic, details.Layout);
        Assert.Null(details.Error);
        Assert.Equal(2, details.Saves.Count);
        Assert.Equal("2 个存档（未识别布局）", details.SummaryLabel);
        Assert.All(details.Saves, save => Assert.Equal(SaveArtifactKind.Generic, save.Kind));

        var readable = Assert.Single(details.Saves, save => save.FileName == "custom_a.sv");
        Assert.Equal("custom_a.sv", readable.Label);
        Assert.True(readable.IsReadable);
        Assert.Equal(2, readable.LayerIndex);
        Assert.Equal("第 3 层", readable.LayerName);
        Assert.Equal("轻松", readable.DifficultyLabel);
        Assert.Equal(99, readable.CharacterId);

        var binary = Assert.Single(details.Saves, save => save.FileName == "custom_b.sav");
        Assert.False(binary.IsReadable);
        Assert.NotNull(binary.Error);
        Assert.False(string.IsNullOrWhiteSpace(binary.ContentHash));
    }

    [Fact]
    public void ReadGroup_MixedVanillaAndCustom_KeepsEveryFile()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "save.sv", VanillaPayload(1, 10, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteSave(group, "custom_1.sv", VanillaPayload(1, 20, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteSave(group, "custom_2.sv", VanillaPayload(1, 30, SaveDifficultyPresets.Normal.EffectiveSettings));

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.Generic, details.Layout);
        Assert.Equal(3, details.Saves.Count);
        Assert.Contains(details.Saves, save => save.FileName == "save.sv" && save.Label == "存档 1");
        Assert.Contains(details.Saves, save => save.FileName == "custom_1.sv" && save.Label == "custom_1.sv");
        Assert.Contains(details.Saves, save => save.FileName == "custom_2.sv" && save.Label == "custom_2.sv");
    }

    [Fact]
    public void ReadGroup_RecursesIntoSubdirectories_ButSkipsReportsAndHiddenFiles()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "custom_a.sv", VanillaPayload(0, 10, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteSave(group, Path.Combine("sub", "custom_b.sv"), VanillaPayload(0, 20, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteSave(group, Path.Combine("sub", "casualty_reports", "report.sv"), VanillaPayload(0, 30, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteSave(group, Path.Combine("sub", ".hidden.sv"), VanillaPayload(0, 40, SaveDifficultyPresets.Normal.EffectiveSettings));

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.Generic, details.Layout);
        Assert.Equal(2, details.Saves.Count);
        Assert.Contains(details.Saves, save => save.FileName == "custom_a.sv");
        Assert.Contains(details.Saves, save => save.FileName == "custom_b.sv");
    }

    [Fact]
    public void ReadGroup_SkipsMpSaveDirectory_SoMostRecentIsARealSlot()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(0, 60, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteText(group, "slot_1.sv.meta", Meta(new Dictionary<string, object?>
        {
            ["Timestamp"] = "2026/11/14 23:00"
        }));
        // Multiplayer session data: newer than the slot, and its own save.sv.
        WriteSave(group, Path.Combine("mp_save", "_session", "save.sv"),
            VanillaPayload(0, 600, SaveDifficultyPresets.Normal.EffectiveSettings));

        var details = Reader.ReadGroup(group);

        var save = Assert.Single(details.Saves);
        Assert.Equal("槽 1", save.Label);
        Assert.Equal("槽 1", details.MostRecentLabel);
        Assert.DoesNotContain("save.sv", details.SummaryLabel);
    }

    [Fact]
    public void ReadGroup_UnreadableSlot_ReportsUnknownFormatWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        File.WriteAllBytes(Path.Combine(group, "slot_1.sv"), [0xDE, 0xAD, 0xBE, 0xEF, 0x00]);

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.MultiSlot, details.Layout);
        var save = Assert.Single(details.Saves);
        Assert.Equal("槽 1", save.Label);
        Assert.Equal(1, save.SlotNumber);
        Assert.False(save.IsReadable);
        Assert.NotNull(save.Error);
        Assert.Equal(64, save.ContentHash.Length);
        Assert.Equal(1, details.SlotCount);
    }

    [Fact]
    public void ReadGroup_MissingOrEmptyGroup_ReturnsEmptyDetails()
    {
        using var temp = new TempDirectory();
        var empty = temp.CreateDirectory("empty");

        var emptyDetails = Reader.ReadGroup(empty);

        Assert.Equal(SaveLayoutKind.Empty, emptyDetails.Layout);
        Assert.Empty(emptyDetails.Saves);
        Assert.Equal(0, emptyDetails.SlotCount);
        Assert.Equal(0, emptyDetails.DeclaredSlotCount);
        Assert.Equal(0, emptyDetails.EmptySlotCount);
        Assert.Null(emptyDetails.MostRecentLabel);
        Assert.Equal("尚无存档文件", emptyDetails.SummaryLabel);
        Assert.Null(emptyDetails.Error);

        var missingDetails = Reader.ReadGroup(Path.Combine(temp.Path, "does-not-exist"));

        Assert.Equal(SaveLayoutKind.Empty, missingDetails.Layout);
        Assert.Empty(missingDetails.Saves);
        Assert.Equal("尚无存档文件", missingDetails.SummaryLabel);
        Assert.NotNull(missingDetails.Error);
    }

    [Fact]
    public void ReadGroup_CorruptMetadataAndContent_DegradePerFile()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(1, 60, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteText(group, "slot_1.sv.meta", "{ this is not json");
        WriteText(group, "slot_2.sv.meta", Meta(new Dictionary<string, object?>
        {
            ["LocationName"] = "坏文件",
            ["Timestamp"] = "2026-01-01T00:00:00Z"
        }));
        File.WriteAllBytes(Path.Combine(group, "slot_2.sv"), [0x1F, 0x8B, 0x08, 0x00, 0x00]);

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.MultiSlot, details.Layout);
        Assert.Equal(2, details.Saves.Count);

        var brokenMeta = Assert.Single(details.Saves, save => save.SlotNumber == 1);
        Assert.True(brokenMeta.IsReadable);
        Assert.False(brokenMeta.HasMetadata);
        Assert.Null(brokenMeta.LocationName);
        Assert.NotNull(brokenMeta.Error);

        var brokenContent = Assert.Single(details.Saves, save => save.SlotNumber == 2);
        Assert.False(brokenContent.IsReadable);
        Assert.NotNull(brokenContent.Error);
        Assert.Equal("坏文件", brokenContent.LocationName);
        Assert.True(brokenContent.HasMetadata);
        Assert.Equal(64, brokenContent.ContentHash.Length);
    }

    [Fact]
    public void ReadGroup_LanguageFileOverridesBuiltInNames_AndMissingLangFallsBack()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "save.sv", VanillaPayload(4, 10, SaveDifficultyPresets.Normal.EffectiveSettings));

        var withoutLang = Reader.ReadGroup(group, temp.CreateDirectory("bare-game"));
        Assert.Equal("第 5 层", withoutLang.Saves[0].LayerName);
        Assert.Equal("艰难", withoutLang.Saves[0].DifficultyLabel);

        var game = temp.CreateDirectory("game");
        WriteLang(game, new Dictionary<string, string>
        {
            ["/other/layertitle5"] = "深层设施",
            ["/other/presetnormal"] = "艰难"
        });

        var withLang = Reader.ReadGroup(group, game);
        Assert.Equal("深层设施", withLang.Saves[0].LayerName);
        Assert.Equal("艰难", withLang.Saves[0].DifficultyLabel);
        Assert.Equal("normal", withLang.Saves[0].DifficultyKey);
    }

    [Fact]
    public void ReadGroup_DifficultyPresets_MatchNormalRelaxedAndFallBackToCustom()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");

        WriteSave(group, "slot_1.sv", VanillaPayload(0, 10, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteSave(group, "slot_2.sv", VanillaPayload(0, 10, SaveDifficultyPresets.Relaxed.EffectiveSettings));
        var custom = new Dictionary<string, SaveSettingValue>(
            SaveDifficultyPresets.Relaxed.EffectiveSettings, StringComparer.Ordinal)
        {
            ["enemystrength"] = SaveSettingValue.FromNumber(
                SaveDifficultyPresets.Relaxed.EffectiveSettings["enemystrength"].Number + 0.5)
        };
        WriteSave(group, "slot_3.sv", VanillaPayload(0, 10, custom));

        var details = Reader.ReadGroup(group);

        Assert.Equal("艰难", Assert.Single(details.Saves, save => save.SlotNumber == 1).DifficultyLabel);
        Assert.Equal("轻松", Assert.Single(details.Saves, save => save.SlotNumber == 2).DifficultyLabel);
        var customSave = Assert.Single(details.Saves, save => save.SlotNumber == 3);
        Assert.Equal("自定义", customSave.DifficultyLabel);
        Assert.Equal(SaveDifficultyPresets.CustomKey, customSave.DifficultyKey);
    }

    [Fact]
    public void ReadGroup_EqualTimestamps_PreferTheLowestSlotNumberForMostRecent()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        var timestamp = "2026-04-01T00:00:00Z";

        WriteSave(group, "slot_1.sv", VanillaPayload(0, 10, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteText(group, "slot_1.sv.meta", Meta(new Dictionary<string, object?> { ["Timestamp"] = timestamp }));
        WriteSave(group, "slot_2.sv", VanillaPayload(0, 20, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteText(group, "slot_2.sv.meta", Meta(new Dictionary<string, object?> { ["Timestamp"] = timestamp }));

        var details = Reader.ReadGroup(group);

        Assert.Equal("槽 1", details.MostRecentLabel);
    }

    [Fact]
    public void DifficultyPresets_ExposeSixNamedPresetsWithOverrides()
    {
        Assert.Equal(6, SaveDifficultyPresets.All.Count);
        Assert.Equal(
            ["normal", "relaxed", "paradise", "desolate", "unchipped", "endless"],
            SaveDifficultyPresets.All.Select(preset => preset.Key).ToList());
        Assert.All(SaveDifficultyPresets.All, preset => Assert.False(string.IsNullOrWhiteSpace(preset.Label)));
        Assert.All(SaveDifficultyPresets.All.Where(preset => preset != SaveDifficultyPresets.Normal),
            preset => Assert.NotEmpty(preset.Overrides));
        Assert.All(SaveDifficultyPresets.All, preset => Assert.NotEmpty(preset.EffectiveSettings));
        Assert.Empty(SaveDifficultyPresets.Normal.Overrides);
    }

    [Fact]
    public void ReadGroup_LegacyMetaWithoutSvSuffix_IsStillRead()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(1, 60, SaveDifficultyPresets.Relaxed.EffectiveSettings));
        // 旧版读取器写的是 slot_1.meta；仍兼容，但真实约定是 slot_1.sv.meta。
        WriteText(group, "slot_1.meta", Meta(new Dictionary<string, object?>
        {
            ["LocationName"] = "旧格式",
            ["Timestamp"] = "2026-01-01T00:00:00Z"
        }));

        var details = Reader.ReadGroup(group);

        var save = Assert.Single(details.Saves);
        Assert.True(save.HasMetadata);
        Assert.Equal("旧格式", save.LocationName);
        Assert.EndsWith("slot_1.meta", save.MetaPath);
    }

    [Fact]
    public void ReadGroup_EmptyQolSlotsArray_DoesNotTurnVanillaSaveIntoMultiSlot()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteText(group, "qol_slots.json", """{"Slots":[],"Names":{}}""");
        WriteSave(group, "save.sv", VanillaPayload(2, 30, SaveDifficultyPresets.Normal.EffectiveSettings));

        var details = Reader.ReadGroup(group);

        Assert.Equal(SaveLayoutKind.SingleSave, details.Layout);
        Assert.Equal(SaveArtifactKind.SingleSave, Assert.Single(details.Saves).Kind);
        Assert.Equal(0, details.DeclaredSlotCount);
        Assert.Empty(details.EmptySlots);
        Assert.Equal(1, details.SlotCount);
    }

    [Fact]
    public void ReadGroup_QolWithNoSaveFiles_ReportsDeclaredSlotsAsEmpty()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteText(group, "qol_slots.json", """{"Slots":[1,2,3],"Names":{}}""");

        var details = Reader.ReadGroup(group);

        Assert.Empty(details.Saves);
        Assert.Equal(3, details.DeclaredSlotCount);
        Assert.Equal(new[] { 1, 2, 3 }, details.EmptySlots);
        Assert.Equal("尚无存档文件", details.SummaryLabel);
    }

    [Fact]
    public void ReadGroup_UnknownRunSettingKey_FallsBackToCustom()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "save.sv", TypedPayload(
            SaveDifficultyPresets.NormalValues,
            ("newSettingFromFutureVersion", SaveSettingValue.FromNumber(5))));

        var save = Assert.Single(Reader.ReadGroup(group).Saves);

        Assert.Equal(SaveDifficultyPresets.CustomKey, save.DifficultyKey);
        Assert.Equal("自定义", save.DifficultyLabel);
    }

    [Fact]
    public void ReadGroup_TextRunSettingNeverMatchesANumericPreset()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "save.sv", TypedPayload(
            SaveDifficultyPresets.NormalValues,
            ("timelimit", SaveSettingValue.FromText("60"))));

        var save = Assert.Single(Reader.ReadGroup(group).Saves);

        Assert.Equal(SaveDifficultyPresets.CustomKey, save.DifficultyKey);
    }

    [Fact]
    public void ReadGroup_GzipExpansionOverLimit_IsReportedWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        var path = Path.Combine(group, "save.sv");

        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            var block = new byte[1024 * 1024];
            Array.Fill(block, (byte)' ');
            var remaining = SaveGroupArchiveLimits.MaxSaveJsonBytes + 1024 * 1024;
            while (remaining > 0)
            {
                var size = (int)Math.Min(block.Length, remaining);
                gzip.Write(block, 0, size);
                remaining -= size;
            }
        }

        var details = Reader.ReadGroup(group);

        var save = Assert.Single(details.Saves);
        Assert.False(save.IsReadable);
        Assert.Contains("解压上限", save.Error);
        Assert.Equal(new FileInfo(path).Length, save.FileSize);
    }

    [Fact]
    public void ReadGroup_AmbiguousLegacyMeta_IsIgnoredInsteadOfMispasted()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(1, 60, SaveDifficultyPresets.Relaxed.EffectiveSettings));
        WriteSave(group, "slot_1.sav", VanillaPayload(2, 90, SaveDifficultyPresets.Normal.EffectiveSettings));
        // Old-style sidecar; slot_1.sv and slot_1.sav would both claim it.
        WriteText(group, "slot_1.meta", Meta(new Dictionary<string, object?>
        {
            ["LocationName"] = "来源不明",
            ["Timestamp"] = "2026-01-01T00:00:00Z"
        }));

        var details = Reader.ReadGroup(group);

        var sv = Assert.Single(details.Saves, save => save.FileName == "slot_1.sv");
        var sav = Assert.Single(details.Saves, save => save.FileName == "slot_1.sav");
        Assert.False(sv.HasMetadata);
        Assert.False(sav.HasMetadata);
        Assert.Null(sv.LocationName);
        Assert.Null(sav.LocationName);
    }

    [Fact]
    public void ReadGroup_MergedSlotKeepsQolCustomName()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteText(group, "qol_slots.json", """{"Slots":[5],"Names":{"5":"结局存档"}}""");
        WriteSave(group, "slot_5.sv", VanillaPayload(6, 120, SaveDifficultyPresets.Normal.EffectiveSettings));
        File.Copy(Path.Combine(group, "slot_5.sv"), Path.Combine(group, "save.sv"));

        var details = Reader.ReadGroup(group);

        var save = Assert.Single(details.Saves);
        Assert.Equal("结局存档", save.CustomName);
        Assert.Contains("结局存档", save.Label);
        Assert.Contains("结局存档", details.SummaryLabel);
    }

    [Fact]
    public void ReadGroup_LocalMetaTimestamp_IsNotShiftedByTimeZone()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(0, 60, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteText(group, "slot_1.sv.meta", Meta(new Dictionary<string, object?>
        {
            ["Timestamp"] = "2026/11/14 23:00",
            ["BiomeIndex"] = 0
        }));

        var save = Assert.Single(Reader.ReadGroup(group).Saves);

        // 游戏写的是本地墙上时间，不能按 UTC 解析后再 +8 小时。
        Assert.Equal("2026-11-14 23:00", save.SavedAt!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    }

    [Fact]
    public void ReadGroup_NestedLanguageFile_ResolvesLayerNames()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        var game = temp.CreateDirectory("game");
        var langDirectory = Path.Combine(game, "CasualtiesUnknown_Data", "Lang");
        Directory.CreateDirectory(langDirectory);
        File.WriteAllText(
            Path.Combine(langDirectory, "zh-CN.json"),
            """{"name":"简体中文","other":{"layertitle1":"砾石荒原","layertitle2":"深层砾石带","presetnormal":"艰难"}}""",
            new UTF8Encoding(false));

        WriteSave(group, "slot_1.sv", VanillaPayload(0, 60, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteSave(group, "slot_2.sv", VanillaPayload(1, 90, SaveDifficultyPresets.Relaxed.EffectiveSettings));

        var details = Reader.ReadGroup(group, game);

        Assert.Equal("砾石荒原", Assert.Single(details.Saves, save => save.SlotNumber == 1).LayerName);
        Assert.Equal("深层砾石带", Assert.Single(details.Saves, save => save.SlotNumber == 2).LayerName);
        Assert.Equal("艰难", Assert.Single(details.Saves, save => save.SlotNumber == 1).DifficultyLabel);
    }

    [Fact]
    public void ReadGroup_CharacterIndex_MapsToOfficialNames()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        var game = temp.CreateDirectory("game");
        WriteLang(game, new Dictionary<string, string>
        {
            ["/other/experiment"] = "Experiment",
            ["/other/milky"] = "Milky",
            ["/other/dune"] = "Dune"
        });

        WriteSave(group, "slot_1.sv", VanillaPayload(0, 10, SaveDifficultyPresets.Normal.EffectiveSettings, character: 0));
        WriteSave(group, "slot_2.sv", VanillaPayload(0, 20, SaveDifficultyPresets.Normal.EffectiveSettings, character: 1));
        WriteSave(group, "slot_3.sv", VanillaPayload(0, 30, SaveDifficultyPresets.Normal.EffectiveSettings, character: 2));

        var details = Reader.ReadGroup(group, game);

        Assert.Equal("Experiment", Assert.Single(details.Saves, s => s.SlotNumber == 1).CharacterLabel);
        Assert.Equal("Milky", Assert.Single(details.Saves, s => s.SlotNumber == 2).CharacterLabel);
        Assert.Equal("Dune", Assert.Single(details.Saves, s => s.SlotNumber == 3).CharacterLabel);
    }

    [Fact]
    public void ReadGroup_CharacterWithoutLang_UsesBuiltInNames()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(0, 10, SaveDifficultyPresets.Normal.EffectiveSettings, character: 1));

        var save = Assert.Single(Reader.ReadGroup(group).Saves);

        Assert.Equal("Milky", save.CharacterLabel);
    }

    [Fact]
    public void ReadGroup_CharacterOutOfRange_KeepsRawValueInLabel()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(0, 10, SaveDifficultyPresets.Normal.EffectiveSettings, character: 9));
        WriteSave(group, "slot_2.sv", VanillaPayload(0, 20, SaveDifficultyPresets.Normal.EffectiveSettings, character: -1));
        WriteSave(group, "slot_3.sv", VanillaPayload(0, 30, SaveDifficultyPresets.Normal.EffectiveSettings, character: 1.5));

        var details = Reader.ReadGroup(group);

        Assert.Equal("未知角色（9）", Assert.Single(details.Saves, s => s.SlotNumber == 1).CharacterLabel);
        Assert.Equal("未知角色（-1）", Assert.Single(details.Saves, s => s.SlotNumber == 2).CharacterLabel);
        Assert.Equal("未知角色（1.5）", Assert.Single(details.Saves, s => s.SlotNumber == 3).CharacterLabel);
    }

    [Fact]
    public void ReadGroup_MissingOrNonNumericCharacter_LeavesLabelEmpty()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(0, 10, SaveDifficultyPresets.Normal.EffectiveSettings, character: null));
        var nonNumeric = (Dictionary<string, object?>)VanillaPayload(
            0, 20, SaveDifficultyPresets.Normal.EffectiveSettings, character: 1);
        nonNumeric["character"] = "not-a-number";
        WriteSave(group, "slot_2.sv", nonNumeric);

        var details = Reader.ReadGroup(group);

        Assert.Null(Assert.Single(details.Saves, s => s.SlotNumber == 1).CharacterLabel);
        Assert.Null(Assert.Single(details.Saves, s => s.SlotNumber == 2).CharacterLabel);
    }

    [Fact]
    public void ReadGroup_SummaryMentionsTheCharacter()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(0, 10, SaveDifficultyPresets.Normal.EffectiveSettings, character: 1));
        WriteText(group, "slot_1.sv.meta", Meta(new Dictionary<string, object?>
        {
            ["Timestamp"] = "2026/11/16 20:00"
        }));

        var details = Reader.ReadGroup(group);

        Assert.Contains("Milky", details.SummaryLabel);
        Assert.StartsWith("槽 1", details.MostRecentLabel);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("array")]
    [InlineData("null")]
    public void ReadGroup_UnknownRunSettingWithUnsupportedType_DoesNotMatchAPreset(string kind)
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        object? value = kind switch
        {
            "object" => new Dictionary<string, object?> { ["nested"] = 1 },
            "array" => new[] { 1, 2 },
            _ => null
        };
        WriteSave(group, "save.sv", PayloadWithSetting("newSettingFromFutureVersion", value));

        var save = Assert.Single(Reader.ReadGroup(group).Saves);

        Assert.Equal(SaveDifficultyPresets.CustomKey, save.DifficultyKey);
        Assert.Equal("自定义", save.DifficultyLabel);
    }

    [Fact]
    public void ReadGroup_KnownRunSettingWithUnsupportedType_DoesNotMatchAPreset()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "save.sv", PayloadWithSetting("timelimit", new Dictionary<string, object?> { ["value"] = 60 }));

        var save = Assert.Single(Reader.ReadGroup(group).Saves);

        Assert.Equal(SaveDifficultyPresets.CustomKey, save.DifficultyKey);
    }

    [Fact]
    public void ReadGroup_SummaryShowsPlayTime_AndDetailKeepsTheSaveTime()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        WriteSave(group, "slot_1.sv", VanillaPayload(1, 3725.5, SaveDifficultyPresets.Normal.EffectiveSettings));
        WriteText(group, "slot_1.sv.meta", Meta(new Dictionary<string, object?>
        {
            ["Timestamp"] = "2026/11/14 23:00"
        }));

        var details = Reader.ReadGroup(group);

        var save = Assert.Single(details.Saves);
        Assert.Equal("1 小时 2 分", save.RunTimeLabel);
        Assert.Contains("1 小时 2 分", details.SummaryLabel);
        Assert.DoesNotContain("23:00", details.SummaryLabel);
        Assert.Equal("2026-11-14 23:00", save.SavedAtLabel);
    }

    [Fact]
    public void ReadGroup_SummaryOmitsPlayTime_WhenTheSaveHasNone()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        var payload = (Dictionary<string, object?>)VanillaPayload(
            0, 10, SaveDifficultyPresets.Normal.EffectiveSettings);
        payload.Remove("runTime");
        WriteSave(group, "save.sv", payload);

        var details = Reader.ReadGroup(group);

        var save = Assert.Single(details.Saves);
        Assert.Equal(string.Empty, save.RunTimeLabel);
        Assert.DoesNotContain("小时", details.SummaryLabel);
        Assert.DoesNotContain(" 分", details.SummaryLabel);
    }

    [Fact]
    public void ReadGroup_ZeroByteSave_IsListedAsAnEmptyFileWithoutAnError()
    {
        using var temp = new TempDirectory();
        var group = temp.CreateDirectory("group");
        File.WriteAllBytes(Path.Combine(group, "slot_1.sv"), []);
        WriteSave(group, "slot_2.sv", VanillaPayload(0, 10, SaveDifficultyPresets.Normal.EffectiveSettings));

        var details = Reader.ReadGroup(group);

        Assert.Equal(2, details.Saves.Count);
        var empty = Assert.Single(details.Saves, save => save.SlotNumber == 1);
        Assert.True(empty.IsReadable);
        Assert.True(empty.IsEmptyFile);
        Assert.Null(empty.Error);
        Assert.Equal(0, empty.FileSize);
        Assert.Equal("槽 1", empty.Label);

        var readable = Assert.Single(details.Saves, save => save.SlotNumber == 2);
        Assert.False(readable.IsEmptyFile);
    }

    private static object PayloadWithSetting(string key, object? value)
    {
        var settings = SaveDifficultyPresets.NormalValues
            .Select(pair => new Dictionary<string, object?>
            {
                ["Item1"] = pair.Key,
                ["Item2"] = pair.Value.Kind switch
                {
                    SaveSettingKind.Boolean => pair.Value.Boolean,
                    SaveSettingKind.Text => pair.Value.Text,
                    _ => pair.Value.Number
                }
            })
            .ToList();
        settings.Add(new Dictionary<string, object?> { ["Item1"] = key, ["Item2"] = value });

        return new Dictionary<string, object?>
        {
            ["biome"] = 0,
            ["runTime"] = 10,
            ["runSettings"] = settings,
            ["cId"] = 1
        };
    }

    private static object TypedPayload(
        IReadOnlyDictionary<string, SaveSettingValue> settings,
        params (string Key, SaveSettingValue Value)[] overrides)
    {
        var pairs = settings
            .Select(pair => (pair.Key, pair.Value))
            .ToList();
        foreach (var (key, value) in overrides)
        {
            pairs.RemoveAll(pair => pair.Key == key);
            pairs.Add((key, value));
        }

        return new Dictionary<string, object?>
        {
            ["biome"] = 0,
            ["runTime"] = 10,
            ["runSettings"] = pairs.Select(pair => new Dictionary<string, object?>
            {
                ["Item1"] = pair.Key,
                ["Item2"] = pair.Value.Kind switch
                {
                    SaveSettingKind.Boolean => pair.Value.Boolean,
                    SaveSettingKind.Text => pair.Value.Text,
                    _ => pair.Value.Number
                }
            }).ToArray(),
            ["cId"] = 1
        };
    }

    private static void WriteLang(string gameDirectory, Dictionary<string, string> values)
    {
        var directory = Path.Combine(gameDirectory, "CasualtiesUnknown_Data", "Lang");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "zh-CN.json"), JsonSerializer.Serialize(values), new UTF8Encoding(false));
    }

    private static string Meta(Dictionary<string, object?> values) => JsonSerializer.Serialize(values);

    private static object VanillaPayload(
        int biome,
        double runTime,
        IReadOnlyDictionary<string, SaveSettingValue> settings,
        int characterId = 4242,
        double? character = 0)
    {
        var payload = new Dictionary<string, object?>
        {
            ["biome"] = biome,
            ["runTime"] = runTime,
            ["runSettings"] = settings
                .Select(pair => new Dictionary<string, object?>
                {
                    ["Item1"] = pair.Key,
                    ["Item2"] = pair.Value.Kind switch
                    {
                        SaveSettingKind.Boolean => pair.Value.Boolean,
                        SaveSettingKind.Text => pair.Value.Text,
                        _ => pair.Value.Number
                    }
                })
                .ToArray(),
            ["cId"] = characterId,
            ["isMercRun"] = false
        };

        if (character.HasValue) payload["character"] = character.Value;
        return payload;
    }

    private static string WriteSave(string group, string relativePath, object payload)
    {
        var path = Path.Combine(group, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            writer.Write(JsonSerializer.Serialize(payload));
        }

        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }

    private static void WriteText(string group, string relativePath, string content)
    {
        var path = Path.Combine(group, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
