namespace ModificationsKnown.Core.Saves;

/// <summary>
/// One save file as shown in the UI. Fields are filled as far as the layout and
/// the file contents allow; anything the save does not expose stays null/empty
/// rather than being guessed.
/// </summary>
public sealed class SaveSlotMetadata
{
    public string FilePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long FileSize { get; set; }

    /// <summary>Lowercase hex SHA-256 of the file; empty when the file could not be read.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public SaveArtifactKind Kind { get; set; } = SaveArtifactKind.Generic;

    /// <summary>Display label, e.g. "槽 5", "当前进度", "自动存档" or the file name.</summary>
    public string Label { get; set; } = string.Empty;

    public int? SlotNumber { get; set; }

    /// <summary>Custom slot name from QoL's <c>qol_slots.json</c> Names map.</summary>
    public string? CustomName { get; set; }

    /// <summary>Sidecar <c>.meta</c> path when the layout writes one.</summary>
    public string? MetaPath { get; set; }

    public string? LocationName { get; set; }

    public double? DepthMeters { get; set; }

    /// <summary>Zero-based biome index from the save or its metadata.</summary>
    public int? LayerIndex { get; set; }

    /// <summary>Localised layer title resolved from the game's Lang file.</summary>
    public string? LayerName { get; set; }

    /// <summary>Preset key (normal/relaxed/...) or <c>custom</c>.</summary>
    public string? DifficultyKey { get; set; }

    public string? DifficultyLabel { get; set; }

    public double? IgtSeconds { get; set; }

    /// <summary>Human readable play time, e.g. "1 小时 2 分".</summary>
    public string RunTimeLabel { get; set; } = string.Empty;

    public DateTimeOffset? SavedAt { get; set; }

    public string SavedAtLabel { get; set; } = string.Empty;

    /// <summary>Layout specific save kind (QoL writes e.g. "Manual").</summary>
    public string? SaveKind { get; set; }

    public bool IsMultiplayer { get; set; }

    /// <summary>
    /// Raw <c>character</c> value from the save (0 = Experiment, 1 = Milky,
    /// 2 = Dune). Kept as a double so out-of-range values survive for display.
    /// </summary>
    public double? CharacterValue { get; set; }

    /// <summary>Localised character name, or “未知角色（n）” for unknown values.</summary>
    public string? CharacterLabel { get; set; }

    public bool IsMercRun { get; set; }

    public bool Cheated { get; set; }

    public long? SeedValue { get; set; }

    /// <summary>Vanilla character id (<c>cId</c>).</summary>
    public int? CharacterId { get; set; }

    public int ModCount { get; set; }

    public bool IsModded { get; set; }

    /// <summary>False when the file is binary garbage or a broken gzip/JSON payload.</summary>
    public bool IsReadable { get; set; } = true;

    /// <summary>True for a 0-byte file: shown as an empty placeholder, not as an
    /// unreadable save.</summary>
    public bool IsEmptyFile { get; set; }

    public string? Error { get; set; }

    public bool IsMostRecent { get; set; }

    /// <summary>True when a sidecar metadata file was found and parsed.</summary>
    public bool HasMetadata { get; set; }
}
