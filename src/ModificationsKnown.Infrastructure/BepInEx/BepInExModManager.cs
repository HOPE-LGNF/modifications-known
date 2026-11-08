using System.Security.Cryptography;
using System.Text.Json;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;

namespace ModificationsKnown.Infrastructure.BepInEx;

public sealed class BepInExModManager : IModManager
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IAppLogger? _logger;

    public BepInExModManager(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<InstalledMod>> ScanAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ScanCore(installation, cancellationToken), cancellationToken);
    }

    public Task<OperationResult> SetEnabledAsync(
        GameInstallation installation,
        InstalledMod mod,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => SetEnabledCore(installation, mod, enabled, cancellationToken), cancellationToken);
    }

    public Task<OperationResult> AdoptAsync(
        GameInstallation installation,
        InstalledMod mod,
        PackageSource? source = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => AdoptCore(installation, mod, source, cancellationToken), cancellationToken);
    }

    public Task<OperationResult> UninstallManagedAsync(
        GameInstallation installation,
        InstalledMod mod,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => UninstallManagedCore(installation, mod, cancellationToken), cancellationToken);
    }

    public Task<OperationResult> DeleteWholePackageAsync(
        GameInstallation installation,
        InstalledMod mod,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DeleteWholeCore(installation, mod, cancellationToken), cancellationToken);
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

        _logger?.Debug($"Scanning plugins (recursive DLL discovery): {pluginsDirectory}");
        var mods = new List<InstalledMod>();

        foreach (var directory in Directory.EnumerateDirectories(pluginsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dllFiles = EnumerateDllFiles(directory).ToList();
            if (dllFiles.Count == 0) continue;

            var manifest = ReadManifest(directory);
            var plugins = dllFiles.Select(ReadPlugin).ToList();
            var comparison = manifest.Manifest is null
                ? new ManifestComparison([], [], [])
                : CompareManifest(directory, manifest.Manifest);

            mods.Add(new InstalledMod
            {
                PackageId = Path.GetFileName(directory),
                DirectoryPath = directory,
                Plugins = plugins,
                Manifest = manifest.Manifest,
                ManifestState = manifest.State,
                ManifestError = manifest.Error,
                CanAdopt = manifest.State == PackageManifestState.Unmanaged,
                MissingFiles = comparison.Missing,
                ModifiedFiles = comparison.Modified,
                UnexpectedFiles = comparison.Unexpected,
                CanDeleteWhole = true
            });
        }

        // Loose DLLs directly in plugins are kept read-only: there is no reliable
        // ownership boundary for a single file.
        foreach (var file in Directory.EnumerateFiles(pluginsDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(IsDllFile)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            mods.Add(new InstalledMod
            {
                PackageId = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file)),
                DirectoryPath = pluginsDirectory,
                Plugins = [ReadPlugin(file)],
                ManifestState = PackageManifestState.Unmanaged,
                CanDeleteWhole = false,
                CanAdopt = false
            });
        }

        _logger?.Debug($"Scanned {mods.Count} package(s), {mods.Count(mod => mod.IsEnabled)} enabled, {mods.Count(mod => mod.IsManaged)} managed");
        // Keep the list order stable when the user toggles a package. Sorting by
        // enabled state would make the row jump to the bottom after every click.
        return mods
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (PackageManifest? Manifest, PackageManifestState State, string? Error) ReadManifest(string directory)
    {
        var manifestPath = PackageManifestReader.GetManifestPath(directory);
        if (!File.Exists(manifestPath))
            return (null, PackageManifestState.Unmanaged, null);

        return PackageManifestReader.TryRead(directory, out var manifest, out var error)
            ? (manifest, PackageManifestState.Managed, null)
            : (null, PackageManifestState.Broken, error);
    }

    private static ManifestComparison CompareManifest(string directory, PackageManifest manifest)
    {
        var expected = manifest.Files
            .GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // Disabling a BepInEx plugin renames foo.dll to foo.dll.disabled.
        // That is a state change, not a missing/extra file. Compare logical
        // paths with the .disabled suffix stripped so the file-state badges do
        // not turn into "missing N / extra N" after every disable.
        var actual = EnumeratePackageFiles(directory)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                var logical = relative.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? relative[..^".disabled".Length]
                    : relative;
                return new ActualPackageFile(relative, logical, path);
            })
            .ToList();

        var actualByLogical = actual
            .GroupBy(file => file.LogicalRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        var modified = new List<string>();

        foreach (var file in expected.Values)
        {
            if (!actualByLogical.TryGetValue(file.RelativePath, out var actualFile))
            {
                missing.Add(file.RelativePath);
                continue;
            }

            try
            {
                if (new FileInfo(actualFile.FullPath).Length != file.Size
                    || !string.Equals(HashFile(actualFile.FullPath), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    modified.Add(file.RelativePath);
            }
            catch
            {
                modified.Add(file.RelativePath);
            }
        }

        var unexpected = actual
            .Where(file => !expected.ContainsKey(file.LogicalRelativePath))
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ManifestComparison(missing, modified, unexpected);
    }

    private static IEnumerable<string> EnumerateDllFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(IsDllFile)
            .Where(path => !IsInsideManifestDirectory(directory, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumeratePackageFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !IsInsideManifestDirectory(directory, path));

    private static bool IsInsideManifestDirectory(string packageDirectory, string filePath)
    {
        var manifestDirectory = Path.Combine(packageDirectory, PackageManifest.DirectoryName);
        return PathSafety.IsWithinDirectory(filePath, manifestDirectory);
    }

    private static bool IsDllFile(string path) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase);

    private sealed record ActualPackageFile(string RelativePath, string LogicalRelativePath, string FullPath);

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
        GameInstallation installation,
        InstalledMod mod,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!IsSafePackage(installation, mod, allowRoot: true, out var error))
            return OperationResult.Fail(error);

        var moves = new List<(string Source, string Destination)>();

        foreach (var plugin in mod.Plugins)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!PathSafety.IsWithinDirectory(plugin.FilePath, installation.PluginsDirectory))
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
            foreach (var move in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(move.Destination)) File.Move(move.Destination, move.Source);
                }
                catch
                {
                    // Keep the original error; the UI will rescan and show reality.
                }
            }

            return OperationResult.Fail($"切换失败：{exception.Message}");
        }
    }

    private OperationResult AdoptCore(
        GameInstallation installation,
        InstalledMod mod,
        PackageSource? source,
        CancellationToken cancellationToken)
    {
        if (!IsSafePackage(installation, mod, allowRoot: false, out var error))
            return OperationResult.Fail(error);

        if (mod.ManifestState != PackageManifestState.Unmanaged)
            return OperationResult.Fail("只有未托管包可以接管；已托管或清单异常的包不能重复接管。");

        try
        {
            var files = new List<PackageFile>();
            foreach (var file in EnumeratePackageFiles(mod.DirectoryPath)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(mod.DirectoryPath, file).Replace('\\', '/');
                if (!PackageManifestReader.IsSafeRelativePath(relative))
                    return OperationResult.Fail($"拒绝接管：发现不安全路径 {relative}");

                files.Add(new PackageFile(relative, new FileInfo(file).Length, HashFile(file)));
            }

            if (!files.Any(file => file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                return OperationResult.Fail("这个目录没有 DLL，不能接管为模组包。");

            var manifest = new PackageManifest
            {
                LocalId = Guid.NewGuid().ToString("N"),
                PackageName = mod.PackageId,
                InstalledAt = DateTimeOffset.UtcNow,
                Source = source ?? PackageSource.Local,
                Files = files
            };

            var manifestDirectory = Path.Combine(mod.DirectoryPath, PackageManifest.DirectoryName);
            Directory.CreateDirectory(manifestDirectory);
            var manifestPath = Path.Combine(manifestDirectory, PackageManifest.FileName);
            var temporaryPath = manifestPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, ManifestJsonOptions));
            File.Move(temporaryPath, manifestPath, overwrite: true);

            _logger?.Info($"Adopted unmanaged package: {mod.PackageId} ({files.Count} files)");
            return OperationResult.Ok($"已接管「{mod.PackageId}」，记录 {files.Count} 个文件。");
        }
        catch (Exception exception)
        {
            _logger?.Warn($"Adopt failed for {mod.PackageId}: {exception.Message}");
            return OperationResult.Fail($"接管失败：{exception.Message}");
        }
    }

    private OperationResult UninstallManagedCore(
        GameInstallation installation,
        InstalledMod mod,
        CancellationToken cancellationToken)
    {
        if (!mod.IsManaged || mod.Manifest is null)
            return OperationResult.Fail("这个包没有安装清单，只能使用“删除整个目录”。");

        if (!IsSafePackage(installation, mod, allowRoot: false, out var error))
            return OperationResult.Fail(error);

        var deleted = 0;
        var missing = 0;
        var preserved = 0;

        foreach (var file in mod.Manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!PackageManifestReader.IsSafeRelativePath(file.RelativePath))
            {
                preserved++;
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(
                mod.DirectoryPath,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!PathSafety.IsWithinDirectory(path, mod.DirectoryPath))
            {
                preserved++;
                continue;
            }

            if (!File.Exists(path))
            {
                missing++;
                continue;
            }

            try
            {
                if (string.Equals(HashFile(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    deleted++;
                }
                else
                {
                    // The user changed this file. Never delete it silently.
                    preserved++;
                }
            }
            catch
            {
                preserved++;
            }
        }

        TryRemoveManifestDirectory(mod.DirectoryPath);
        CleanupEmptyDirectories(mod.DirectoryPath);

        var remaining = Directory.Exists(mod.DirectoryPath)
            ? Directory.EnumerateFileSystemEntries(mod.DirectoryPath, "*", SearchOption.AllDirectories).Any()
            : false;

        _logger?.Info($"Uninstalled managed package {mod.PackageId}: deleted={deleted}, missing={missing}, preserved={preserved}");
        return OperationResult.Ok(
            preserved > 0
                ? $"已移除 {deleted} 个托管文件；保留了 {preserved} 个用户新增/修改文件{(missing > 0 ? $"，{missing} 个文件已不存在" : "")}。"
                : $"已卸载「{mod.PackageId}」{(deleted > 0 ? $"，移除 {deleted} 个文件" : "")}。{(remaining ? "目录中仍有用户文件。" : "")}");
    }

    private OperationResult DeleteWholeCore(
        GameInstallation installation,
        InstalledMod mod,
        CancellationToken cancellationToken)
    {
        if (!IsSafePackage(installation, mod, allowRoot: false, out var error))
            return OperationResult.Fail(error);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(mod.DirectoryPath, recursive: true);
            _logger?.Info($"Package directory deleted: {mod.DirectoryPath}");
            return OperationResult.Ok("已删除整个包目录。");
        }
        catch (Exception exception)
        {
            return OperationResult.Fail($"删除失败：{exception.Message}");
        }
    }

    private static bool IsSafePackage(
        GameInstallation installation,
        InstalledMod mod,
        bool allowRoot,
        out string error)
    {
        error = string.Empty;
        var pluginsRoot = Path.GetFullPath(installation.PluginsDirectory);
        var packagePath = Path.GetFullPath(mod.DirectoryPath);

        if (!Directory.Exists(packagePath))
        {
            error = "包目录不存在。";
            return false;
        }

        if (!PathSafety.IsWithinDirectory(packagePath, pluginsRoot))
        {
            error = "拒绝操作：目标不在当前游戏的 BepInEx/plugins 内。";
            return false;
        }

        if (!allowRoot
            && string.Equals(
                packagePath.TrimEnd(Path.DirectorySeparatorChar),
                pluginsRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            error = "拒绝操作：根目录 DLL 不能按包删除。";
            return false;
        }

        return true;
    }

    private static void TryRemoveManifestDirectory(string packageDirectory)
    {
        var manifestDirectory = Path.Combine(packageDirectory, PackageManifest.DirectoryName);
        if (!Directory.Exists(manifestDirectory)) return;

        try
        {
            Directory.Delete(manifestDirectory, recursive: true);
        }
        catch
        {
            // If a user put files there, keep them; they are not managed.
        }
    }

    private static void CleanupEmptyDirectories(string packageDirectory)
    {
        if (!Directory.Exists(packageDirectory)) return;

        foreach (var directory in Directory.EnumerateDirectories(packageDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(packageDirectory).Any())
                Directory.Delete(packageDirectory);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record ManifestComparison(
        IReadOnlyList<string> Missing,
        IReadOnlyList<string> Modified,
        IReadOnlyList<string> Unexpected);
}
