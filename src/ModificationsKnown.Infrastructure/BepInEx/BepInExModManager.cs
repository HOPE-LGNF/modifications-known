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

        // A managed package may own a plugins-root DLL as an auxiliary file
        // (Together/CUCoreLib). Keep it out of the loose-DLL list so it cannot
        // appear as an unmanaged duplicate that cannot be toggled or removed.
        var claimedAuxiliary = mods
            .SelectMany(mod => mod.AuxiliaryFiles)
            .SelectMany(file =>
            {
                var path = Path.GetFullPath(Path.Combine(
                    installation.GameDirectory,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                return new[] { path, path + ".disabled" };
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Loose DLLs directly in plugins are kept read-only: there is no reliable
        // ownership boundary for a single file.
        foreach (var file in Directory.EnumerateFiles(pluginsDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(IsDllFile)
                     .Where(file => !claimedAuxiliary.Contains(Path.GetFullPath(file)))
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
                CanAdopt = true
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
        if (string.Equals(
                Path.GetFullPath(mod.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(installation.PluginsDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return AdoptLooseDllCore(installation, mod, source, cancellationToken);
        }

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

            if (!files.Any(file => IsDllFile(file.RelativePath)))
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

    private OperationResult AdoptLooseDllCore(
        GameInstallation installation,
        InstalledMod mod,
        PackageSource? source,
        CancellationToken cancellationToken)
    {
        if (mod.Plugins.Count != 1)
            return OperationResult.Fail("这个裸 DLL 条目包含多个插件，无法安全判断包边界。");

        var dllPath = mod.Plugins[0].FilePath;
        if (!File.Exists(dllPath)
            || !IsDllFile(dllPath)
            || !PathSafety.IsWithinDirectory(dllPath, installation.PluginsDirectory))
            return OperationResult.Fail("裸 DLL 路径无效或不在 plugins 根目录。");

        var baseName = Path.GetFileNameWithoutExtension(dllPath);
        if (baseName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^".dll".Length];
        baseName = PathSafety.GetSafeFileName(baseName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "LooseMod";

        var target = Path.Combine(installation.PluginsDirectory, baseName);
        if (Directory.Exists(target) || File.Exists(target))
            return OperationResult.Fail($"已存在同名目标「{baseName}」；未移动任何文件。");

        var companions = Directory.EnumerateFiles(installation.PluginsDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(path, dllPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => IsCompanionOf(path, baseName))
            .ToList();
        var moved = new List<(string Source, string Destination)>();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(target);

            void MoveOne(string source)
            {
                var destination = Path.Combine(target, Path.GetFileName(source));
                if (File.Exists(destination))
                    throw new IOException($"目标已存在：{Path.GetFileName(destination)}");
                File.Move(source, destination);
                moved.Add((source, destination));
            }

            MoveOne(dllPath);
            foreach (var companion in companions)
                MoveOne(companion);

            var files = moved
                .Select(pair => new PackageFile(
                    Path.GetRelativePath(target, pair.Destination).Replace('\\', '/'),
                    new FileInfo(pair.Destination).Length,
                    HashFile(pair.Destination)))
                .ToList();

            var manifest = new PackageManifest
            {
                LocalId = Guid.NewGuid().ToString("N"),
                PackageName = baseName,
                InstalledAt = DateTimeOffset.UtcNow,
                Source = source ?? PackageSource.Local,
                Files = files
            };
            var manifestDirectory = Path.Combine(target, PackageManifest.DirectoryName);
            Directory.CreateDirectory(manifestDirectory);
            var manifestPath = Path.Combine(manifestDirectory, PackageManifest.FileName);
            var temporaryPath = manifestPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, ManifestJsonOptions));
            File.Move(temporaryPath, manifestPath, overwrite: true);

            _logger?.Info($"Adopted loose DLL into package: {baseName} ({files.Count} files)");
            return OperationResult.Ok($"已把裸 DLL 及其 {companions.Count} 个同名附属文件移入「{baseName}」并接管。");
        }
        catch (Exception exception)
        {
            _logger?.Warn($"Loose DLL adopt failed for {baseName}: {exception.Message}");
            foreach (var (original, destination) in moved.AsEnumerable().Reverse())
            {
                try { if (File.Exists(destination) && !File.Exists(original)) File.Move(destination, original); } catch { }
            }
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch { }
            return OperationResult.Fail($"接管裸 DLL 失败：{exception.Message}");
        }
    }

    private static bool IsCompanionOf(string path, string baseName)
    {
        var name = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^".dll".Length];
        if (!string.Equals(stem, baseName, StringComparison.OrdinalIgnoreCase))
            return false;

        var extension = Path.GetExtension(name);
        return extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".config", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase);
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

            // A disabled plugin is stored as "foo.dll.disabled". The manifest
            // still records "foo.dll", so check both names before calling the
            // file missing; otherwise uninstall leaves the disabled DLL behind
            // and the package reappears as an unmanaged folder.
            var actualPath = File.Exists(path)
                ? path
                : File.Exists(path + ".disabled")
                    ? path + ".disabled"
                    : null;

            if (actualPath is null)
            {
                missing++;
                continue;
            }

            try
            {
                if (string.Equals(HashFile(actualPath), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(actualPath);
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

        foreach (var aux in mod.Manifest.AuxiliaryFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!PackageManifestReader.IsSafeRelativePath(aux.RelativePath))
            {
                preserved++;
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(
                installation.GameDirectory,
                aux.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!PathSafety.IsWithinDirectory(path, installation.GameDirectory))
            {
                preserved++;
                continue;
            }

            var variants = new[] { path, path + ".disabled" }
                .Where(File.Exists)
                .ToList();

            if (variants.Count == 0)
            {
                missing++;
                continue;
            }

            if (IsAuxiliaryClaimedByOther(installation, mod, aux.RelativePath))
            {
                preserved++;
                continue;
            }

            foreach (var actualPath in variants)
            {
                try
                {
                    if (string.Equals(HashFile(actualPath), aux.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(actualPath);
                        deleted++;
                    }
                    else
                    {
                        preserved++;
                    }
                }
                catch
                {
                    preserved++;
                }
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

    private static bool IsAuxiliaryClaimedByOther(
        GameInstallation installation,
        InstalledMod current,
        string relativePath)
    {
        var currentDirectory = Path.GetFullPath(current.DirectoryPath);
        foreach (var directory in Directory.EnumerateDirectories(installation.PluginsDirectory))
        {
            if (string.Equals(Path.GetFullPath(directory), currentDirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!PackageManifestReader.TryRead(directory, out var manifest, out _))
                continue;

            if (manifest is not null && manifest.AuxiliaryFiles.Any(file =>
                    string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
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
