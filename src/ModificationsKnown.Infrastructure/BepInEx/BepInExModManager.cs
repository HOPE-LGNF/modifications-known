using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;

namespace ModificationsKnown.Infrastructure.BepInEx;

public sealed class BepInExModManager : IModManager
{
    private readonly IAppLogger? _logger;

    public BepInExModManager(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scans immediate plugin folders plus loose DLLs in BepInEx/plugins.
    /// A folder is treated as one package so multi-DLL mods are enabled and
    /// disabled together.
    /// </summary>
    public Task<IReadOnlyList<InstalledMod>> ScanAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ScanCore(installation, cancellationToken), cancellationToken);
    }

    public Task<OperationResult> SetEnabledAsync(
        InstalledMod mod,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => SetEnabledCore(mod, enabled, cancellationToken), cancellationToken);
    }

    public Task<OperationResult> DeleteAsync(
        InstalledMod mod,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DeleteCore(mod, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<InstalledMod> ScanCore(
        GameInstallation installation,
        CancellationToken cancellationToken)
    {
        var pluginsDirectory = installation.PluginsDirectory;
        if (!Directory.Exists(pluginsDirectory))
        {
            _logger?.Debug($"Plugins directory not found: {pluginsDirectory}");
            return [];
        }

        _logger?.Debug($"Scanning plugins: {pluginsDirectory}");
        var mods = new List<InstalledMod>();

        foreach (var directory in Directory.EnumerateDirectories(pluginsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pluginFiles = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(directory, "*.dll.disabled", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pluginFiles.Count == 0) continue;

            var plugins = pluginFiles.Select(ReadPlugin).ToList();

            mods.Add(new InstalledMod
            {
                PackageId = Path.GetFileName(directory),
                DirectoryPath = directory,
                Plugins = plugins,
                CanDelete = true
            });
        }

        // BepInEx also loads loose DLLs directly in plugins. They are shown, but
        // deletion is disabled because a loose DLL may be a shared dependency.
        foreach (var file in Directory.EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.EnumerateFiles(pluginsDirectory, "*.dll.disabled", SearchOption.TopDirectoryOnly))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            mods.Add(new InstalledMod
            {
                PackageId = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file)),
                DirectoryPath = pluginsDirectory,
                Plugins = [ReadPlugin(file)],
                CanDelete = false
            });
        }

        _logger?.Debug($"Scanned {mods.Count} package(s), {mods.Count(mod => mod.IsEnabled)} enabled");
        return mods
            .OrderByDescending(mod => mod.IsEnabled)
            .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static InstalledPlugin ReadPlugin(string filePath)
    {
        var metadata = BepInExAssemblyReader.Read(filePath);
        var enabled = !filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

        return new InstalledPlugin(
            filePath,
            Path.GetFileName(filePath),
            metadata.Guid,
            metadata.Name,
            metadata.Version,
            enabled,
            metadata.Dependencies);
    }

    private OperationResult SetEnabledCore(
        InstalledMod mod,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var root = Path.GetDirectoryName(mod.DirectoryPath) is { } parent
            ? parent
            : mod.DirectoryPath;

        var moves = new List<(string Source, string Destination)>();

        foreach (var plugin in mod.Plugins)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!PathSafety.IsWithinDirectory(plugin.FilePath, root))
                return OperationResult.Fail("拒绝操作：插件路径不在 BepInEx/plugins 目录内。");

            if (enabled)
            {
                if (!plugin.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;

                var destination = plugin.FilePath[..^".disabled".Length];
                if (File.Exists(destination))
                    return OperationResult.Fail($"无法启用：目标文件已存在 {Path.GetFileName(destination)}");

                moves.Add((plugin.FilePath, destination));
            }
            else
            {
                if (plugin.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;

                var destination = plugin.FilePath + ".disabled";
                if (File.Exists(destination))
                    return OperationResult.Fail($"无法禁用：目标文件已存在 {Path.GetFileName(destination)}");

                moves.Add((plugin.FilePath, destination));
            }
        }

        if (moves.Count == 0)
            return OperationResult.Ok("状态未变化。");

        var applied = new List<(string Source, string Destination)>();
        try
        {
            foreach (var move in moves)
            {
                File.Move(move.Source, move.Destination);
                applied.Add(move);
            }

            _logger?.Info($"Package {(enabled ? "enabled" : "disabled")}: {mod.PackageId}");
            return OperationResult.Ok(enabled ? "已启用。" : "已禁用。");
        }
        catch (Exception exception)
        {
            // Best-effort rollback so a package is never left half-toggled.
            foreach (var move in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(move.Destination))
                        File.Move(move.Destination, move.Source);
                }
                catch
                {
                    // Keep the original error; the UI will rescan and show reality.
                }
            }

            return OperationResult.Fail($"切换失败：{exception.Message}");
        }
    }

    private OperationResult DeleteCore(InstalledMod mod, CancellationToken cancellationToken)
    {
        if (!mod.CanDelete)
            return OperationResult.Fail("根目录 DLL 不允许由此处删除。");

        var pluginsRoot = mod.DirectoryPath.EndsWith("plugins", StringComparison.OrdinalIgnoreCase)
            ? mod.DirectoryPath
            : Path.GetDirectoryName(mod.DirectoryPath);

        if (string.IsNullOrWhiteSpace(pluginsRoot)
            || !Directory.Exists(mod.DirectoryPath)
            || !PathSafety.IsWithinDirectory(mod.DirectoryPath, pluginsRoot)
            || string.Equals(
                Path.GetFullPath(mod.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(pluginsRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail("拒绝删除：目标不是插件目录内的独立包。");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(mod.DirectoryPath, recursive: true);
            _logger?.Info($"Package deleted: {mod.DirectoryPath}");
            return OperationResult.Ok("已删除。");
        }
        catch (Exception exception)
        {
            return OperationResult.Fail($"删除失败：{exception.Message}");
        }
    }
}
