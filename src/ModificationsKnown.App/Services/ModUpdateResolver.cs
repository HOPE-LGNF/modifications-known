using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Nexus;

namespace ModificationsKnown.App.Services;

public sealed record LatestFile(NexusModFile File, NexusFileVersion Version);

/// <summary>Shared update target/version resolution for Discover and Installed.
/// Keeps package identity and "highest file version" rules in one place.</summary>
public sealed class ModUpdateResolver
{
    private readonly INexusModsClient _nexus;
    private readonly ModOperationService _operations;

    public ModUpdateResolver(INexusModsClient nexus, ModOperationService operations)
    {
        _nexus = nexus;
        _operations = operations;
    }

    public async Task<LatestFile?> ResolveLatestAsync(
        NexusMatch match,
        CancellationToken cancellationToken = default)
    {
        var files = await _nexus.GetFilesAsync(match.ModUid, cancellationToken);
        if (files.Count == 0) return null;

        LatestFile? best = null;
        foreach (var file in files)
        {
            var versions = await _nexus.GetFileVersionsAsync(file.Id, cancellationToken);
            foreach (var version in versions)
            {
                if (string.IsNullOrWhiteSpace(version.Version)) continue;

                if (best is null
                    || VersionText.IsNewer(version.Version, best.Version.Version)
                    || (string.Equals(version.Version.Trim(), best.Version.Version.Trim(), StringComparison.OrdinalIgnoreCase)
                        && version.UploadedAt > best.Version.UploadedAt))
                {
                    best = new LatestFile(file, version);
                }
            }
        }

        if (best is not null) return best;

        var fallbackFile = files
            .OrderByDescending(candidate => candidate.IsActive)
            .ThenByDescending(candidate => candidate.LastUploadedAt)
            .FirstOrDefault();
        if (fallbackFile is null) return null;

        var fallbackVersions = await _nexus.GetFileVersionsAsync(fallbackFile.Id, cancellationToken);
        var fallback = fallbackVersions.FirstOrDefault(candidate => candidate.IsPrimary)
                       ?? fallbackVersions.OrderByDescending(candidate => candidate.UploadedAt).FirstOrDefault();
        return fallback is null ? null : new LatestFile(fallbackFile, fallback);
    }

    public async Task<InstalledMod?> FindTargetAsync(
        GameInstallation installation,
        string nexusName,
        string gameScopedId,
        CancellationToken cancellationToken = default)
    {
        var safeName = PathSafety.GetSafeFileName(nexusName);
        var installed = await _operations.ScanAsync(installation, cancellationToken);
        return installed.FirstOrDefault(mod =>
            string.Equals(mod.Source?.GameScopedId, gameScopedId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mod.PackageId, safeName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mod.Name, nexusName, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPluginsRoot(GameInstallation installation, InstalledMod mod) =>
        string.Equals(
            Path.GetFullPath(mod.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(installation.PluginsDirectory).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
