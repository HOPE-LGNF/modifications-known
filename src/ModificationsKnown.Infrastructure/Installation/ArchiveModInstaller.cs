using System.Text;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Infrastructure.BepInEx;
using ModificationsKnown.Infrastructure.Storage;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ModificationsKnown.Infrastructure.Installation;

/// <summary>
/// Installs a downloaded archive into BepInEx/plugins. Extraction is isolated
/// in a temporary directory; package folders are staged first and only moved
/// into place after validation.
/// </summary>
public sealed class ArchiveModInstaller : IModInstaller
{
    private readonly IAppLogger? _logger;

    public ArchiveModInstaller(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public Task<ModInstallResult> InstallArchiveAsync(
        GameInstallation installation,
        string archivePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logger?.Info($"Installing archive: {archivePath}");
        return Task.Run(
            () => InstallCore(installation, archivePath, progress, cancellationToken),
            cancellationToken);
    }

    private ModInstallResult InstallCore(
        GameInstallation installation,
        string archivePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
            return ModInstallResult.Failure("安装包不存在。");

        if (!Directory.Exists(installation.PluginsDirectory))
            return ModInstallResult.Failure("找不到 BepInEx/plugins 目录。请先安装 BepInEx。");

        var extension = Path.GetExtension(archivePath).ToLowerInvariant();
        if (extension is not (".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2"))
            return ModInstallResult.Failure($"不支持的压缩格式：{extension}");

        Directory.CreateDirectory(AppPaths.TempDirectory);
        var tempRoot = Path.Combine(AppPaths.TempDirectory, "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            progress?.Report(0.05);
            ExtractSafely(archivePath, tempRoot, cancellationToken);
            progress?.Report(0.45);

            var pluginsRoot = FindBepInExPluginsDirectory(tempRoot);
            var packages = pluginsRoot is not null
                ? CollectPackagesFromPluginsRoot(pluginsRoot, archivePath)
                : CollectPackagesFromArchiveRoot(tempRoot, archivePath);

            if (packages.Count == 0)
                return ModInstallResult.Failure("压缩包里没有找到可安装的 DLL。");

            var results = new List<InstalledPackageResult>();

            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = InstallPackage(installation.PluginsDirectory, package.SourceDirectory, package.PackageName);
                _logger?.Debug($"Install package result: {package.PackageName} -> {result.Message}");
                results.Add(new InstalledPackageResult(package.PackageName, result.Success, result.Message));

                progress?.Report(0.45 + 0.5 * (results.Count / (double)packages.Count));
            }

            var installed = results.Count(result => result.Success);
            var failed = results.Count - installed;
            var message = installed == 0
                ? results.FirstOrDefault(result => !result.Success)?.Message ?? "安装失败。"
                : failed == 0
                    ? $"已安装 {installed} 个模组包。"
                    : $"已安装 {installed} 个，{failed} 个失败：{string.Join("；", results.Where(result => !result.Success).Select(result => result.Message))}";

            return new ModInstallResult(installed > 0, message, results);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ModInstallResult.Failure($"安装失败：{exception.Message}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
            progress?.Report(1);
        }
    }

    private static void ExtractSafely(string archivePath, string destinationRoot, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = ArchiveFactory.OpenArchive(stream, new ReaderOptions());

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;
            if (string.IsNullOrWhiteSpace(entry.Key)) continue;

            var key = entry.Key.Replace('\\', '/');
            if (!PathSafety.IsSafeRelativePath(key))
                throw new InvalidDataException($"压缩包包含不安全的路径：{entry.Key}");

            var destination = Path.GetFullPath(Path.Combine(destinationRoot, key));
            if (!PathSafety.IsWithinDirectory(destination, destinationRoot))
                throw new InvalidDataException($"压缩包条目越界：{entry.Key}");

            var parent = Path.GetDirectoryName(destination);
            if (parent is not null) Directory.CreateDirectory(parent);
            entry.WriteToFile(destination, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
        }
    }

    private static string? FindBepInExPluginsDirectory(string root)
    {
        foreach (var candidates in Directory.EnumerateDirectories(root, "plugins", SearchOption.AllDirectories))
        {
            var parent = Directory.GetParent(candidates);
            if (parent is not null
                && string.Equals(parent.Name, "BepInEx", StringComparison.OrdinalIgnoreCase))
                return candidates;
        }

        return null;
    }

    private static List<PackageCandidate> CollectPackagesFromPluginsRoot(string pluginsRoot, string archivePath)
    {
        var packages = new List<PackageCandidate>();

        foreach (var directory in Directory.EnumerateDirectories(pluginsRoot))
        {
            if (Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any())
                packages.Add(new PackageCandidate(directory, Path.GetFileName(directory)));
        }

        var looseDlls = Directory.EnumerateFiles(pluginsRoot, "*.dll", SearchOption.TopDirectoryOnly).ToList();
        if (looseDlls.Count == 0) return packages;

        var name = ReadPackageName(looseDlls[0])
                   ?? Path.GetFileNameWithoutExtension(archivePath)
                   ?? "ImportedMod";

        // Copy loose DLLs into one staging package. This is intentionally a
        // separate package from subdirectories so multiple root plugins remain
        // distinguishable where possible.
        var staging = Path.Combine(pluginsRoot, ".mk-loose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (var file in Directory.EnumerateFiles(pluginsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(staging, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
        }

        packages.Add(new PackageCandidate(staging, name));
        return packages;
    }

    private static List<PackageCandidate> CollectPackagesFromArchiveRoot(string root, string archivePath)
    {
        var packages = new List<PackageCandidate>();
        var directories = Directory.EnumerateDirectories(root).ToList();
        var rootDlls = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly).ToList();

        if (rootDlls.Count > 0)
        {
            var name = ReadPackageName(rootDlls[0])
                       ?? Path.GetFileNameWithoutExtension(archivePath)
                       ?? "ImportedMod";
            packages.Add(new PackageCandidate(root, name));
            return packages;
        }

        if (directories.Count == 1)
        {
            var directory = directories[0];
            var name = FindFirstPluginName(directory) ?? Path.GetFileName(directory);
            if (Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any())
                packages.Add(new PackageCandidate(directory, name));
            return packages;
        }

        foreach (var directory in directories)
        {
            var name = FindFirstPluginName(directory) ?? Path.GetFileName(directory);
            if (Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any())
                packages.Add(new PackageCandidate(directory, name));
        }

        return packages;
    }

    private static string? FindFirstPluginName(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
                .Select(ReadPackageName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadPackageName(string dllPath)
    {
        var metadata = BepInExAssemblyReader.Read(dllPath);
        return string.IsNullOrWhiteSpace(metadata.Name) ? null : metadata.Name;
    }

    private static OperationResult InstallPackage(string pluginsRoot, string sourceDirectory, string packageName)
    {
        if (!Directory.Exists(sourceDirectory))
            return OperationResult.Fail("安装源目录不存在。");

        var safeName = PathSafety.GetSafeFileName(packageName);
        var target = Path.Combine(pluginsRoot, safeName);
        if (Directory.Exists(target))
            return OperationResult.Fail($"「{safeName}」已存在，未覆盖现有版本。");

        var staging = Path.Combine(pluginsRoot, ".mk-staging-" + Guid.NewGuid().ToString("N"));
        var moved = false;

        try
        {
            CopyDirectory(sourceDirectory, staging);
            if (!Directory.EnumerateFileSystemEntries(staging).Any())
                return OperationResult.Fail("安装包为空。");

            Directory.Move(staging, target);
            moved = true;
            return OperationResult.Ok($"已安装「{safeName}」。");
        }
        catch (Exception exception)
        {
            return OperationResult.Fail($"安装「{safeName}」失败：{exception.Message}");
        }
        finally
        {
            if (!moved) TryDeleteDirectory(staging);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            var target = Path.Combine(destination, relative);
            if (!PathSafety.IsWithinDirectory(target, destination))
                throw new InvalidDataException("安装包目录越界。");
            Directory.CreateDirectory(target);
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            if (!PathSafety.IsWithinDirectory(target, destination))
                throw new InvalidDataException("安装包文件越界。");

            var parent = Path.GetDirectoryName(target);
            if (parent is not null) Directory.CreateDirectory(parent);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Temporary cleanup is best effort.
        }
    }

    private sealed record PackageCandidate(string SourceDirectory, string PackageName);
}
