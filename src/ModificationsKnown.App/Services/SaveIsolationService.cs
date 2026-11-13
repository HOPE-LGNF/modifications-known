using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

public sealed record SaveIsolationResult(bool Success, string Message, string? ProfilePath = null);

/// <summary>
/// The full game and the demo share %LocalLow%\Orsoniks\CasualtiesUnknown and
/// both write save.sv there. Before a launch we point that one directory at a
/// per-installation profile via an NTFS junction. The game then writes save.sv
/// directly into the selected profile, so Demo saves cannot overwrite full-game
/// slots (and vice versa).
/// </summary>
public sealed class SaveIsolationService
{
    private readonly IAppLogger? _logger;

    public SaveIsolationService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public string ProfilesRoot => AppPaths.SaveProfilesDirectory;

    public string GetLiveSaveDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Orsoniks", "CasualtiesUnknown");

    public string GetProfileDirectory(GameInstallation installation)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(installation.GameDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant()));
        var id = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Path.Combine(ProfilesRoot, id);
    }

    public Task<SaveIsolationResult> PrepareAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new SaveIsolationResult(true, "非 Windows 环境，跳过存档隔离。"));

        return Task.Run(() => PrepareCore(installation, cancellationToken), cancellationToken);
    }

    private SaveIsolationResult PrepareCore(GameInstallation installation, CancellationToken cancellationToken)
    {
        var live = GetLiveSaveDirectory();
        var profile = GetProfileDirectory(installation);
        var parent = Path.GetDirectoryName(live)!;

        try
        {
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(profile);

            if (Directory.Exists(live))
            {
                var info = new DirectoryInfo(live);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var target = info.LinkTarget;
                    if (string.IsNullOrWhiteSpace(target))
                        return new SaveIsolationResult(false, "现有存档目录是链接，但无法读取目标；请手动检查。");

                    var targetFull = Path.GetFullPath(target);
                    if (!IsUnder(targetFull, ProfilesRoot))
                        return new SaveIsolationResult(false, $"现有存档链接指向 MK 之外的位置：{targetFull}。未修改。");

                    if (PathEquals(targetFull, profile))
                        return new SaveIsolationResult(true, "存档已隔离到当前版本。", profile);

                    // Retarget the junction. The old target keeps its files.
                    Directory.Delete(live, recursive: false);
                    if (!CreateJunction(live, profile, out var linkError))
                    {
                        CreateJunction(live, targetFull, out _);
                        return new SaveIsolationResult(false, $"切换存档隔离失败，已尝试恢复原链接：{linkError}");
                    }
                    return new SaveIsolationResult(true, "存档已切换到当前版本。", profile);
                }

                if (!Directory.EnumerateFileSystemEntries(live).Any())
                {
                    Directory.Delete(live, recursive: false);
                }
                else if (!HasRealSaveFiles(profile))
                {
                    CopyDirectory(live, profile);
                    var backup = Path.Combine(
                        AppPaths.RootDirectory, "save-backups",
                        $"{Path.GetFileName(profile)}-{DateTime.UtcNow:yyyyMMddHHmmss}");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    Directory.Move(live, backup);
                    _logger?.Info($"Original save directory backed up to {backup}");
                    if (!CreateJunction(live, profile, out var firstLinkError))
                    {
                        try { Directory.Move(backup, live); } catch { }
                        return new SaveIsolationResult(false, $"创建存档隔离失败，已尝试恢复原目录：{firstLinkError}");
                    }
                    File.WriteAllText(Path.Combine(profile, ".mk-save-profile"), installation.GameDirectory);
                    return new SaveIsolationResult(true, "存档已隔离到当前版本。", profile);
                }
                else
                {
                    return new SaveIsolationResult(
                        false,
                        "检测到未隔离的存档目录，但当前版本的存档配置已经有文件。为避免覆盖，未修改；请先手动备份。",
                        profile);
                }
            }

            if (!Directory.Exists(live) && !CreateJunction(live, profile, out var createError))
                return new SaveIsolationResult(false, $"创建存档隔离失败：{createError}", profile);

            File.WriteAllText(Path.Combine(profile, ".mk-save-profile"), installation.GameDirectory);
            return new SaveIsolationResult(true, "存档已隔离到当前版本。", profile);
        }
        catch (Exception exception)
        {
            _logger?.Warn($"Save isolation failed: {exception.Message}");
            return new SaveIsolationResult(false, $"存档隔离失败：{exception.Message}", profile);
        }
    }

    private static bool CreateJunction(string link, string target, out string error)
    {
        try
        {
            var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                error = "无法启动 mklink。";
                return false;
            }

            process.WaitForExit(15000);
            error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode == 0 && Directory.Exists(link)) return true;
            error = string.IsNullOrWhiteSpace(error) ? $"mklink 退出码 {process.ExitCode}" : error;
            return false;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
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

    private static bool HasRealSaveFiles(string profile) =>
        Directory.EnumerateFileSystemEntries(profile)
            .Any(path => !string.Equals(Path.GetFileName(path), ".mk-save-profile", StringComparison.OrdinalIgnoreCase));

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
