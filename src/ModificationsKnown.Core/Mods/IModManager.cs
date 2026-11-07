using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.Core.Mods;

public interface IModManager
{
    Task<IReadOnlyList<InstalledMod>> ScanAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default);

    Task<OperationResult> SetEnabledAsync(
        InstalledMod mod,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteAsync(
        InstalledMod mod,
        CancellationToken cancellationToken = default);
}

public interface IModInstaller
{
    /// <summary>
    /// Installs a downloaded archive into BepInEx/plugins. Implementations must
    /// reject path traversal and must not leave a half-installed package behind.
    /// </summary>
    Task<OperationResult> InstallArchiveAsync(
        GameInstallation installation,
        string archivePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
