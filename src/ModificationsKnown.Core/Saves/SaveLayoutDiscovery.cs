namespace ModificationsKnown.Core.Saves;

/// <summary>How a save group directory is organised on disk.</summary>
public enum SaveLayoutKind
{
    /// <summary>No save files at all (new group, or the directory is gone).</summary>
    Empty,

    /// <summary>The vanilla layout: one <c>save.sv</c> and an optional autosave.</summary>
    SingleSave,

    /// <summary>A multi-slot mod layout, normally QoL's <c>slot_N.sv</c> files.</summary>
    MultiSlot,

    /// <summary>Files exist but no known layout owns them; listed by file name.</summary>
    Generic
}

/// <summary>What one save file represents inside its layout.</summary>
public enum SaveArtifactKind
{
    /// <summary>A numbered manual slot.</summary>
    Slot,

    /// <summary>The most recent save copy (<c>save.sv</c> in a slot layout).</summary>
    Current,

    /// <summary>An autosave copy.</summary>
    Autosave,

    /// <summary>The only save of a vanilla profile.</summary>
    SingleSave,

    /// <summary>An unclaimed file in an unrecognised layout.</summary>
    Generic
}

/// <summary>
/// The result of inspecting one save group directory. The reader never throws:
/// an unreadable group or file is reported through <see cref="Error"/> or
/// per-file <c>IsReadable</c>/<c>Error</c> instead.
/// </summary>
public sealed class SaveGroupDetails
{
    public SaveLayoutKind Layout { get; init; } = SaveLayoutKind.Empty;

    /// <summary>Deduplicated saves, slots first, then autosave/current, then generic.</summary>
    public IReadOnlyList<SaveSlotMetadata> Saves { get; init; } = [];

    /// <summary>Total slot capacity (declared slots or the highest slot seen).</summary>
    public int SlotCount { get; init; }

    /// <summary>Slots announced by the layout itself (QoL <c>qol_slots.json</c>).</summary>
    public int DeclaredSlotCount { get; init; }

    /// <summary>Announced slots without a save file.</summary>
    public int EmptySlotCount { get; init; }

    /// <summary>Slot numbers declared by the layout but missing on disk, ascending.</summary>
    public IReadOnlyList<int> EmptySlots { get; init; } = [];

    /// <summary>Label of the newest save, or null when nothing is dated.</summary>
    public string? MostRecentLabel { get; init; }

    /// <summary>Detailed description of the newest save (slot, depth, location,
    /// difficulty, character and time), or null when nothing is dated.</summary>
    public string? MostRecentDetail { get; init; }

    /// <summary>Short Chinese description for the group list.</summary>
    public string SummaryLabel { get; init; } = "尚无存档文件";

    public string? Error { get; init; }
}
