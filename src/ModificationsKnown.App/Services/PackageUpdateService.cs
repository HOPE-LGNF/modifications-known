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

        var wasEnabled = target.IsEnabled;
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
                cancellationToken,
                new ArchiveInstallOptions(target.PackageId, AllowAuxiliaryFiles: true));

            if (!installed.AllSucceeded)
            {
                var restored = TryRestore(staged, targetPath);
                return new PackageUpdateResult(
                    false,
                    restored
                        ? $"{installed.Message} 已恢复旧版本。"
                        : $"{installed.Message} 更新未完成，且旧版本未能自动恢复；暂存目录：{staged}",
                    backupPath);
            }

            if (!wasEnabled) DisablePackage(targetPath);
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
            var restored = TryRestore(staged, targetPath);
            _logger?.Warn($"Update failed: {exception.Message}; restored={restored}");
            return new PackageUpdateResult(
                false,
                restored
                    ? $"更新失败：{exception.Message}（已恢复旧版本）"
                    : $"更新失败：{exception.Message}；旧版本未能自动恢复，暂存目录：{staged}",
                backupPath);
        }
    }

    private static bool TryRestore(string staged, string target)
    {
        // If staging never happened, target is still the live install. Do not
        // delete it just because a rollback was requested.
        if (!Directory.Exists(staged)) return false;

        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staged, target);
            return true;
        }
        catch
        {
            // Keep the staged copy and report the failed restore to the caller.
            return false;
        }
    }

    private static void DisablePackage(string packagePath)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(packagePath, "*.dll", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;
                var destination = file + ".disabled";
                if (!File.Exists(destination)) File.Move(file, destination);
            }
        }
        catch
        {
            // Best effort: preserving the install is more important than the flag.
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
