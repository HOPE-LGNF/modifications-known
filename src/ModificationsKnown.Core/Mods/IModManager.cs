using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.Core.Mods;

public interface IModManager
{
    Task<IReadOnlyList<InstalledMod>> ScanAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default);

    Task<OperationResult> SetEnabledAsync(
        GameInstallation installation,
        InstalledMod mod,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes only manifest-listed, unchanged files; user files are preserved.</summary>
    /// <summary>Creates a baseline manifest for an existing unmanaged directory.</summary>
    Task<OperationResult> AdoptAsync(
        GameInstallation installation,
        InstalledMod mod,
        PackageSource? source = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> UninstallManagedAsync(
        GameInstallation installation,
        InstalledMod mod,
        CancellationToken cancellationToken = default);

    /// <summary>Dangerous explicit action: removes the whole package directory.</summary>
    Task<OperationResult> DeleteWholePackageAsync(
        GameInstallation installation,
        InstalledMod mod,
        CancellationToken cancellationToken = default);
}

public interface IModInstaller
{
    /// <summary>
    /// Installs a downloaded archive into BepInEx/plugins. Implementations must
    /// reject path traversal and must not leave a half-installed package behind.
    /// </summary>
    Task<ModInstallResult> InstallArchiveAsync(
        GameInstallation installation,
        string archivePath,
        IProgress<double>? progress = null,
        PackageSource? source = null,
        CancellationToken cancellationToken = default);
}
