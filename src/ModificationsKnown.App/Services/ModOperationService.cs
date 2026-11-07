using System.Collections.Concurrent;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;

namespace ModificationsKnown.App.Services;

/// <summary>
/// Serializes writes for one game installation and keeps UI code from talking
/// directly to the scanner/installer. Reads stay lock-free; callers use a
/// generation token to reject stale scan results.
/// </summary>
public sealed class ModOperationService
{
    private readonly IModManager _manager;
    private readonly IModInstaller _installer;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public ModOperationService(IModManager manager, IModInstaller installer)
    {
        _manager = manager;
        _installer = installer;
    }

    public Task<IReadOnlyList<InstalledMod>> ScanAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default) =>
        _manager.ScanAsync(installation, cancellationToken);

    public async Task<ModInstallResult> InstallArchiveAsync(
        GameInstallation installation,
        string archivePath,
        PackageSource? source = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var gate = GetGate(installation);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await _installer.InstallArchiveAsync(
                installation,
                archivePath,
                progress,
                source,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OperationResult> SetEnabledAsync(
        GameInstallation installation,
        InstalledMod mod,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var gate = GetGate(installation);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await _manager.SetEnabledAsync(installation, mod, enabled, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OperationResult> AdoptAsync(
        GameInstallation installation,
        InstalledMod mod,
        PackageSource? source = null,
        CancellationToken cancellationToken = default)
    {
        var gate = GetGate(installation);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await _manager.AdoptAsync(installation, mod, source, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OperationResult> UninstallManagedAsync(
        GameInstallation installation,
        InstalledMod mod,
        CancellationToken cancellationToken = default)
    {
        var gate = GetGate(installation);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await _manager.UninstallManagedAsync(installation, mod, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OperationResult> DeleteWholePackageAsync(
        GameInstallation installation,
        InstalledMod mod,
        CancellationToken cancellationToken = default)
    {
        var gate = GetGate(installation);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await _manager.DeleteWholePackageAsync(installation, mod, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GetGate(GameInstallation installation) =>
        _locks.GetOrAdd(
            Path.GetFullPath(installation.GameDirectory).TrimEnd(Path.DirectorySeparatorChar),
            _ => new SemaphoreSlim(1, 1));
}
