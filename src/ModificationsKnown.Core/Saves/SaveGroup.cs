namespace ModificationsKnown.Core.Saves;

/// <summary>
/// One complete LocalLow save profile. The directory name is the group id, not
/// the user-visible name, so renames never move files on disk.
/// </summary>
public sealed record SaveGroup(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    bool IsDefault,
    string DirectoryPath);

/// <summary>
/// Persisted inside each group directory. It travels with the directory into
/// the Windows recycle bin and lets a scan rebuild the group after an Explorer
/// restore. It intentionally stores the installation key, never an absolute
/// user path.
/// </summary>
public sealed class SaveGroupManifest
{
    public int SchemaVersion { get; set; } = SaveGroupFormat.ManifestSchemaVersion;

    public Guid GroupId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string InstallationKey { get; set; } = string.Empty;

    public bool IsDefault { get; set; }
}
