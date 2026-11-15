using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Infrastructure.Storage;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ModificationsKnown.Infrastructure.Installation;

/// <summary>
/// Installs a user-supplied BepInEx archive into one game instance. The
/// launcher deliberately does not ship or silently download a BepInEx build:
/// the user chooses the matching loader archive, and MK validates its layout
/// before copying anything into the game directory.
/// </summary>
public sealed class BepInExInstaller
{
    private readonly IAppLogger? _logger;

    public BepInExInstaller(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public Task<OperationResult> InstallAsync(
        string gameDirectory,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => InstallCore(gameDirectory, archivePath, cancellationToken), cancellationToken);
    }

    private OperationResult InstallCore(string gameDirectory, string archivePath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(gameDirectory))
            return OperationResult.Fail("游戏目录不存在。");
        if (!File.Exists(archivePath))
            return OperationResult.Fail("BepInEx 压缩包不存在。");
        if (Directory.Exists(Path.Combine(gameDirectory, "BepInEx"))
            || File.Exists(Path.Combine(gameDirectory, "winhttp.dll")))
            return OperationResult.Fail("这个版本已经存在 BepInEx；请先备份并手动清理后再安装。");

        Directory.CreateDirectory(AppPaths.TempDirectory);
        var tempRoot = Path.Combine(AppPaths.TempDirectory, "bepinex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            ExtractSafely(archivePath, tempRoot, cancellationToken);
            var sourceRoot = FindLoaderRoot(tempRoot);
            if (sourceRoot is null)
                return OperationResult.Fail("压缩包里没有找到 BepInEx/ 与 winhttp.dll。");
            if (!Directory.Exists(Path.Combine(sourceRoot, "BepInEx", "core")))
                return OperationResult.Fail("压缩包里缺少 BepInEx/core。");
            if (!Directory.Exists(Path.Combine(sourceRoot, "BepInEx", "plugins")))
                Directory.CreateDirectory(Path.Combine(sourceRoot, "BepInEx", "plugins"));

            // Preflight every destination before copying. This keeps a
            // collision from leaving a half-installed loader behind.
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                var target = Path.Combine(gameDirectory, relative);
                if (File.Exists(target))
                    return OperationResult.Fail($"目标文件已存在，未覆盖：{relative}");
            }

            CopyDirectory(sourceRoot, gameDirectory);
            _logger?.Info($"BepInEx installed into {gameDirectory}");
            return OperationResult.Ok("BepInEx 已安装；重新检测后即可安装模组。");
        }
        catch (Exception exception)
        {
            _logger?.Warn($"BepInEx install failed: {exception.Message}");
            return OperationResult.Fail($"BepInEx 安装失败：{exception.Message}");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static string? FindLoaderRoot(string root)
    {
        if (Directory.Exists(Path.Combine(root, "BepInEx"))
            && File.Exists(Path.Combine(root, "winhttp.dll")))
            return root;

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(Path.Combine(directory, "BepInEx"))
                && File.Exists(Path.Combine(directory, "winhttp.dll")))
                return directory;
        }

        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void ExtractSafely(string archivePath, string destinationRoot, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(archivePath).ToLowerInvariant();
        if (extension is not (".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2"))
            throw new InvalidOperationException($"不支持的压缩格式：{extension}");

        using var stream = File.OpenRead(archivePath);
        using var archive = ArchiveFactory.OpenArchive(stream, new ReaderOptions());
        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = entry.Key?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(key)) continue;

            var target = Path.GetFullPath(Path.Combine(destinationRoot, key));
            if (!target.StartsWith(Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("压缩包包含越界路径。");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.WriteToFile(target, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
        }
    }

    /// <summary>Removes the loader directory and its root loader files. This
    /// necessarily removes every mod/plugin/config under BepInEx.</summary>
    public Task<OperationResult> UninstallAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(gameDirectory))
                return OperationResult.Fail("游戏目录不存在。");

            var removed = new List<string>();
            try
            {
                var bepInEx = Path.Combine(gameDirectory, "BepInEx");
                if (Directory.Exists(bepInEx))
                {
                    Directory.Delete(bepInEx, recursive: true);
                    removed.Add("BepInEx/");
                }

                foreach (var name in new[] { "winhttp.dll", "doorstop_config.ini", ".doorstop_version" })
                {
                    var path = Path.Combine(gameDirectory, name);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        removed.Add(name);
                    }
                }

                if (removed.Count == 0)
                    return OperationResult.Ok("没有找到 BepInEx 文件，可能已经卸载。");

                _logger?.Info($"BepInEx uninstalled from {gameDirectory}: {string.Join(", ", removed)}");
                return OperationResult.Ok(
                    $"已卸载 BepInEx，并删除该实例下的所有模组、插件和配置（{string.Join("、", removed)}）。");
            }
            catch (Exception exception)
            {
                _logger?.Warn($"BepInEx uninstall failed: {exception.Message}");
                return OperationResult.Fail(
                    $"卸载 BepInEx 失败：{exception.Message}" +
                    (removed.Count > 0 ? $" 已删除：{string.Join("、", removed)}" : string.Empty));
            }
        }, cancellationToken);
    }

}
