namespace ModificationsKnown.Core.Saves;

/// <summary>
/// The index of live groups per installation. Deleted groups leave no
/// tombstone: the directory and its manifest are the source of truth, and a
/// scan can rebuild this index.
/// </summary>
public sealed class SaveGroupCatalog
{
    public int SchemaVersion { get; set; } = SaveGroupFormat.CatalogSchemaVersion;

    public List<SaveGroupInstallationEntry> Installations { get; set; } = [];
}

public sealed class SaveGroupInstallationEntry
{
    public string InstallationKey { get; set; } = string.Empty;

    /// <summary>Diagnostic only; never written into an exported archive.</summary>
    public string GameDirectory { get; set; } = string.Empty;

    public List<SaveGroupRecord> Groups { get; set; } = [];

    /// <summary>The group MK will use the next time it launches this install.
    /// Changing it only updates this preference; the junction moves at launch.</summary>
    public Guid? NextLaunchGroupId { get; set; }
}

public sealed class SaveGroupRecord
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsDefault { get; set; }
}
