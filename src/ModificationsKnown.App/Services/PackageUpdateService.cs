using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

public sealed record PackageUpdateResult(bool Success, string Message, string? BackupPath = null);

/// <summary>Replaces an installed package with a downloaded archive in a
/// same-volume staging step, optionally copies a backup elsewhere, and restores
/// the old directory if installation fails.</summary>
public sealed class PackageUpdateService
{
    private readonly ModOperationService _operations;
    private readonly IAppLogger? _logger;

    public PackageUpdateService(ModOperationService operations, IAppLogger? logger = null)
    {
        _operations = operations;
        _logger = logger;
    }

    public async Task<PackageUpdateResult> InstallReplacingAsync(
        GameInstallation installation,
        InstalledMod target,
        string archivePath,
        PackageSource source,
        bool keepBackup,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pluginsRoot = Path.GetFullPath(installation.PluginsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(target.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(targetPath, pluginsRoot, StringComparison.OrdinalIgnoreCase))
            return new PackageUpdateResult(false, "散放在 plugins 根目录的 DLL 无法安全更新。");

        if (!Directory.Exists(targetPath))
            return new PackageUpdateResult(false, "旧版本目录不存在。");

        var staged = targetPath + ".mk-old-" + Guid.NewGuid().ToString("N")[..8];
        var backupPath = keepBackup
            ? Path.Combine(
                AppPaths.RootDirectory,
                "backups",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                Path.GetFileName(targetPath))
            : null;

        try
        {
            // Same volume: this is the rollback copy. A backup elsewhere is a
            // separate copy, so cross-volume backup no longer breaks updates.
            Directory.Move(targetPath, staged);

            if (backupPath is not null)
            {
                try
                {
                    CopyDirectory(staged, backupPath);
                }
                catch (Exception exception)
                {
                    Directory.Move(staged, targetPath);
                    return new PackageUpdateResult(false, $"备份旧版本失败：{exception.Message}");
                }
            }

            var installed = await _operations.InstallArchiveAsync(
                installation,
                archivePath,
                source,
                progress,
                cancellationToken);

            if (!installed.Success)
            {
                TryRestore(staged, targetPath);
                return new PackageUpdateResult(
                    false,
                    $"{installed.Message} 已恢复旧版本。",
                    backupPath);
            }

            TryDelete(staged);
            _logger?.Info($"Updated package {target.PackageId}; backup={backupPath ?? "none"}");
            return new PackageUpdateResult(
                true,
                backupPath is null
                    ? $"已更新「{target.Name}」。"
                    : $"已更新「{target.Name}」；旧版本备份在 {backupPath}。",
                backupPath);
        }
        catch (Exception exception)
        {
            TryRestore(staged, targetPath);
            _logger?.Warn($"Update failed: {exception.Message}");
            return new PackageUpdateResult(false, $"更新失败：{exception.Message}（已尝试恢复旧版本）", backupPath);
        }
    }

    private static void TryRestore(string staged, string target)
    {
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            if (Directory.Exists(staged)) Directory.Move(staged, target);
        }
        catch
        {
            // Report through the caller's message; backup/staging remains on disk.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A leftover staging folder is harmless.
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
