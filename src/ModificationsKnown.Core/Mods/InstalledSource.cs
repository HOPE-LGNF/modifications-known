namespace ModificationsKnown.Core.Mods;

/// <summary>
/// Provenance recorded when the launcher installs a package from Nexus. This is
/// a lightweight bridge until the full package manifest is introduced.
/// </summary>
public sealed record InstalledSource(
    string GameDirectory,
    string PackageName,
    string NexusName,
    string GameDomain,
    string GameScopedId,
    string ModUid,
    string? Version,
    string? FileId,
    DateTimeOffset RecordedAt);

public interface IInstalledSourceStore
{
    InstalledSource? Get(string gameDirectory, string packageName);

    Task RecordAsync(IReadOnlyList<InstalledSource> sources, CancellationToken cancellationToken = default);

    Task<int> RemoveAllAsync(string gameDirectory, CancellationToken cancellationToken = default);
}
