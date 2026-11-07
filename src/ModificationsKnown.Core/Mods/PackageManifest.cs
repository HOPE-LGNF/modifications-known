namespace ModificationsKnown.Core.Mods;

/// <summary>
/// In-package manifest. It describes the files the launcher deployed and the
/// optional Nexus source. The manifest itself is not listed as a managed file.
/// </summary>
public sealed class PackageManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string DirectoryName = ".modifications-known";
    public const string FileName = "manifest.json";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string LocalId { get; init; } = Guid.NewGuid().ToString("N");

    public string PackageName { get; init; } = string.Empty;

    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;

    public PackageSource? Source { get; init; }

    public List<PackageFile> Files { get; init; } = [];
}

public sealed record PackageSource(
    string Provider,
    string? NexusName,
    string? GameDomain,
    string? GameScopedId,
    string? ModUid,
    string? FileId,
    string? Version)
{
    public static PackageSource Local => new("local", null, null, null, null, null, null);

    public static PackageSource Nexus(
        string name,
        string gameDomain,
        string gameScopedId,
        string? modUid,
        string? fileId,
        string? version) =>
        new("nexus", name, gameDomain, gameScopedId, modUid, fileId, version);
}

public sealed record PackageFile(string RelativePath, long Size, string Sha256);

public enum PackageManifestState
{
    Managed,
    Unmanaged,
    Broken
}
