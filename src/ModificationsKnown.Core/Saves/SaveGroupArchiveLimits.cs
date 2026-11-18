namespace ModificationsKnown.Core.Saves;

/// <summary>
/// Resource limits for save-group archives and save files. A real save is a few
/// KB and a whole group is normally under a few MB, so every number here is
/// orders of magnitude above real data; the limits exist so a hostile or broken
/// package cannot exhaust memory or disk. They are enforced on bytes actually
/// read, never only on lengths declared by a manifest or a ZIP directory.
/// </summary>
public static class SaveGroupArchiveLimits
{
    /// <summary>
    /// manifest.json is a JSON index. 4096 entries with a full relative path and
    /// SHA-256 are ~0.5 MB, so the cap is 2 MiB — big enough for any real group,
    /// small enough that the index can never be the memory problem.
    /// </summary>
    public const int MaxManifestBytes = 2 * 1024 * 1024;

    /// <summary>Maximum JSON nesting accepted from a manifest.</summary>
    public const int MaxManifestDepth = 32;

    /// <summary>Files per archive (a group is the LocalLow profile, not a modpack).</summary>
    public const int MaxEntries = 4096;

    /// <summary>Largest single file accepted, compressed or expanded.</summary>
    public const long MaxEntryBytes = 512L * 1024 * 1024;

    /// <summary>Largest total expansion of one archive.</summary>
    public const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Ratio checks only apply past this size, so tiny text fixtures pass.</summary>
    public const long CompressionRatioFloorBytes = 8L * 1024 * 1024;

    /// <summary>Above the floor, an entry may expand at most this many times.</summary>
    public const long MaxCompressionRatio = 200;

    /// <summary>Compressed .sv file size accepted by the slot reader.</summary>
    public const long MaxSaveFileBytes = 64L * 1024 * 1024;

    /// <summary>Expanded JSON size accepted from one .sv payload.</summary>
    public const long MaxSaveJsonBytes = 128L * 1024 * 1024;

    /// <summary>JSON nesting accepted from a save payload.</summary>
    public const int MaxSaveJsonDepth = 64;
}
