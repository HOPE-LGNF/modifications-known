using System.Security.Cryptography;
using System.Text.Json;
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
/// in a temporary directory; each package is staged outside the plugin loading
/// directory on the same volume, gets an in-package manifest, and is moved into
/// place as one commit.
/// </summary>
public sealed class ArchiveModInstaller : IModInstaller
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IAppLogger? _logger;

    public ArchiveModInstaller(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public Task<ModInstallResult> InstallArchiveAsync(
        GameInstallation installation,
        string archivePath,
        IProgress<double>? progress = null,
        PackageSource? source = null,
        CancellationToken cancellationToken = default)
    {
        _logger?.Info($"Installing archive: {archivePath}");
        return Task.Run(
            () => InstallCore(installation, archivePath, progress, source, cancellationToken),
            cancellationToken);
    }

    private ModInstallResult InstallCore(
        GameInstallation installation,
        string archivePath,
        IProgress<double>? progress,
        PackageSource? source,
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
        var stagingRoot = Path.Combine(
            installation.BepInExDirectory,
            PackageManifest.DirectoryName,
            "staging",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(stagingRoot);

        try
        {
            progress?.Report(0.05);
            ExtractSafely(archivePath, tempRoot, cancellationToken);
            progress?.Report(0.45);

            var pluginsRoot = FindBepInExPluginsDirectory(tempRoot);
            var packages = pluginsRoot is not null
                ? CollectPackagesFromPluginsRoot(pluginsRoot)
                : CollectPackagesFromArchiveRoot(tempRoot, archivePath);

            if (packages.Count == 0)
                return ModInstallResult.Failure("压缩包里没有找到可安装的 DLL。");

            // Preflight every target before writing anything. Existing or
            // duplicate package names are reported as failures, but other
            // packages in the same archive may still be committed.
            var planned = new List<PlannedPackage>();
            var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<InstalledPackageResult>();

            foreach (var package in packages)
            {
                var safeName = PathSafety.GetSafeFileName(package.PackageName);
                var target = Path.Combine(installation.PluginsDirectory, safeName);

                if (reservedNames.Contains(safeName) || Directory.Exists(target))
                {
                    var conflictMessage = Directory.Exists(target)
                        ? $"「{safeName}」已存在，未覆盖现有版本。"
                        : $"安装包内有重复的包名「{safeName}」。";
                    results.Add(new InstalledPackageResult(safeName, false, conflictMessage));
                    continue;
                }

                reservedNames.Add(safeName);
                planned.Add(new PlannedPackage(package.SourceDirectory, safeName, target));
            }

            foreach (var package in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var manifest = BuildManifest(package.SourceDirectory, package.PackageName, source, cancellationToken);
                    var packageStaging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(packageStaging);
                    CopyManagedFiles(package.SourceDirectory, packageStaging, manifest, cancellationToken);
                    WriteManifest(packageStaging, manifest);
                    Directory.Move(packageStaging, package.TargetDirectory);

                    results.Add(new InstalledPackageResult(
                        package.PackageName,
                        true,
                        $"已安装「{package.PackageName}」。",
                        manifest.LocalId));
                    _logger?.Debug($"Install package committed: {package.PackageName} -> {package.TargetDirectory}");
                }
                catch (Exception exception)
                {
                    _logger?.Warn($"Install package failed: {package.PackageName}: {exception.Message}");
                    results.Add(new InstalledPackageResult(package.PackageName, false, $"安装「{package.PackageName}」失败：{exception.Message}"));
                }

                progress?.Report(0.45 + 0.5 * (results.Count / (double)Math.Max(1, packages.Count)));
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
            TryDeleteDirectory(stagingRoot);
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
        foreach (var candidate in Directory.EnumerateDirectories(root, "plugins", SearchOption.AllDirectories))
        {
            var parent = Directory.GetParent(candidate);
            if (parent is not null
                && string.Equals(parent.Name, "BepInEx", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private static List<PackageCandidate> CollectPackagesFromPluginsRoot(string pluginsRoot)
    {
        // A package owns a directory. Loose files directly in BepInEx/plugins
        // have no unambiguous ownership, so a strict installer refuses them.
        if (Directory.EnumerateFiles(pluginsRoot, "*", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidDataException("BepInEx/plugins 根目录存在散放文件，无法确定它们属于哪个模组；已拒绝安装。");

        var packages = new List<PackageCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(pluginsRoot))
        {
            if (ContainsDll(directory))
            {
                packages.Add(new PackageCandidate(directory, Path.GetFileName(directory)));
                continue;
            }

            if (Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any())
                throw new InvalidDataException($"目录「{Path.GetFileName(directory)}」没有 DLL，无法确定包归属；已拒绝安装。");
        }

        if (packages.Count == 0)
            throw new InvalidDataException("压缩包的 BepInEx/plugins 下没有可安装的模组目录。");

        return packages;
    }

    private static List<PackageCandidate> CollectPackagesFromArchiveRoot(string root, string archivePath)
    {
        var rootFiles = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).ToList();
        var rootDlls = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly).ToList();
        var directories = Directory.EnumerateDirectories(root).ToList();

        if (rootDlls.Count > 0)
        {
            if (directories.Any(ContainsDll))
                throw new InvalidDataException("压缩包同时包含根目录 DLL 和子目录 DLL，无法确定包边界；已拒绝安装。");

            return [new PackageCandidate(root, ResolvePackageName(root, archivePath))];
        }

        if (rootFiles.Count > 0)
            throw new InvalidDataException("压缩包根目录存在散放文件但没有 DLL，无法确定包归属；已拒绝安装。");
        if (directories.Count == 0)
            throw new InvalidDataException("压缩包为空；已拒绝安装。");

        if (directories.Count == 1)
        {
            if (!ContainsDll(directories[0]))
                throw new InvalidDataException("压缩包内没有 DLL；已拒绝安装。");

            return [new PackageCandidate(directories[0], ResolvePackageName(directories[0], archivePath))];
        }

        var packages = new List<PackageCandidate>();
        foreach (var directory in directories)
        {
            if (!ContainsDll(directory))
            {
                if (Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any())
                    throw new InvalidDataException($"目录「{Path.GetFileName(directory)}」没有 DLL，无法确定包归属；已拒绝安装。");
                continue;
            }

            packages.Add(new PackageCandidate(directory, ResolvePackageName(directory, archivePath)));
        }

        if (packages.Count == 0)
            throw new InvalidDataException("压缩包内没有可安装的 DLL；已拒绝安装。");

        return packages;
    }

    private static string ResolvePackageName(string directory, string archivePath)
    {
        var pluginName = Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
            .Select(ReadPackageName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        return PathSafety.GetSafeFileName(
            pluginName ?? Path.GetFileName(directory) ?? Path.GetFileNameWithoutExtension(archivePath));
    }

    private static string? ReadPackageName(string dllPath)
    {
        var metadata = BepInExAssemblyReader.Read(dllPath);
        return string.IsNullOrWhiteSpace(metadata.Name) ? null : metadata.Name;
    }

    private static bool ContainsDll(string directory) =>
        Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any();

    private static PackageManifest BuildManifest(
        string sourceDirectory,
        string packageName,
        PackageSource? source,
        CancellationToken cancellationToken)
    {
        var files = new List<PackageFile>();
        var root = Path.GetFullPath(sourceDirectory);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(root, file);
            var normalized = relative.Replace('\\', '/');
            if (normalized.StartsWith(PackageManifest.DirectoryName + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!PackageManifestReader.IsSafeRelativePath(normalized))
                throw new InvalidDataException($"安装包包含不安全路径：{relative}");

            var info = new FileInfo(file);
            files.Add(new PackageFile(normalized, info.Length, HashFile(file)));
        }

        if (!files.Any(file => file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("安装包没有可加载的 DLL。");

        return new PackageManifest
        {
            LocalId = Guid.NewGuid().ToString("N"),
            PackageName = packageName,
            InstalledAt = DateTimeOffset.UtcNow,
            Source = source ?? PackageSource.Local,
            Files = files
        };
    }

    private static void CopyManagedFiles(
        string sourceDirectory,
        string destinationDirectory,
        PackageManifest manifest,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory);

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!PackageManifestReader.IsSafeRelativePath(file.RelativePath))
                throw new InvalidDataException($"清单包含不安全路径：{file.RelativePath}");

            var source = Path.GetFullPath(Path.Combine(sourceRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathSafety.IsWithinDirectory(source, sourceRoot) || !File.Exists(source))
                throw new InvalidDataException($"安装包文件不存在：{file.RelativePath}");

            var destination = Path.GetFullPath(Path.Combine(
                destinationDirectory,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathSafety.IsWithinDirectory(destination, destinationDirectory))
                throw new InvalidDataException($"安装包文件越界：{file.RelativePath}");

            var parent = Path.GetDirectoryName(destination);
            if (parent is not null) Directory.CreateDirectory(parent);
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static void WriteManifest(string packageStaging, PackageManifest manifest)
    {
        var directory = Path.Combine(packageStaging, PackageManifest.DirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, PackageManifest.FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, ManifestJsonOptions));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

    private sealed record PlannedPackage(string SourceDirectory, string PackageName, string TargetDirectory);
}
