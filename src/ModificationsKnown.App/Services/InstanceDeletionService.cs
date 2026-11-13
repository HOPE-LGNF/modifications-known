using System.Diagnostics;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

public enum SaveDisposition
{
    Copy,
    Move,
    Keep
}

public sealed record InstanceDeletionPlan(
    SaveDisposition Disposition,
    string? TargetDirectory,
    bool DeleteInstanceFolder);

public sealed record InstanceSaveFile(string RelativePath, long Length)
{
    public string SizeLabel => Length < 1024
        ? $"{Length} B"
        : Length < 1024 * 1024
            ? $"{Length / 1024.0:0.#} KB"
            : $"{Length / 1024.0 / 1024.0:0.#} MB";
}

public sealed record DeletionStepResult(string Name, bool Success, string Message);

public sealed record InstanceDeletionReport(IReadOnlyList<DeletionStepResult> Steps, bool Aborted)
{
    public bool Success => !Aborted && Steps.All(step => step.Success);

    public string Summary
    {
        get
        {
            var failed = Steps.Count(step => !step.Success);
            if (Aborted) return "删除已中止；没有删除实例记录。";
            return failed == 0
                ? "实例已删除。"
                : $"实例删除完成，但有 {failed} 个步骤失败（见下表）。";
        }
    }
}

/// <summary>
/// Removes one MK instance: saves are archived (or left in place), then the
/// alias/hidden record and per-instance metadata are removed, and only then is
/// the optional game folder deleted. Archiving and folder deletion happen
/// before any record is touched so a failure leaves the instance intact.
/// </summary>
public sealed class InstanceDeletionService
{
    private readonly GameInstanceStore _instances;
    private readonly ModGroupStore _groups;
    private readonly NexusManualMatchStore _manualMatches;
    private readonly IInstalledSourceStore _sources;
    private readonly SaveIsolationService _saveIsolation;
    private readonly IAppLogger? _logger;

    public InstanceDeletionService(
        GameInstanceStore instances,
        ModGroupStore groups,
        NexusManualMatchStore manualMatches,
        IInstalledSourceStore sources,
        SaveIsolationService saveIsolation,
        IAppLogger? logger = null)
    {
        _instances = instances;
        _groups = groups;
        _manualMatches = manualMatches;
        _sources = sources;
        _saveIsolation = saveIsolation;
        _logger = logger;
    }

    public string GetProfileDirectory(GameInstallation installation) =>
        _saveIsolation.GetProfileDirectory(installation);

    public IReadOnlyList<InstanceSaveFile> ListSaves(GameInstallation installation)
    {
        var profile = GetProfileDirectory(installation);
        if (!Directory.Exists(profile)) return [];

        try
        {
            return Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(
                    Path.GetFileName(path), ".mk-save-profile", StringComparison.OrdinalIgnoreCase))
                .Select(path => new InstanceSaveFile(
                    Path.GetRelativePath(profile, path),
                    new FileInfo(path).Length))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            _logger?.Warn($"读取实例存档失败：{exception.Message}");
            return [];
        }
    }

    public string SuggestSaveTarget(GameInstallation installation)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var name = PathSafety.GetSafeFileName(installation.DisplayLabel);
        return Path.Combine(documents, "ModificationsKnown Saves", $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}");
    }

    public static bool IsInstanceRunning(GameInstallation installation)
    {
        var root = Path.GetFullPath(installation.GameDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (IsUnder(path, root)) return true;
            }
            catch
            {
                // Access denied for other users' processes; skip them.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    public async Task<InstanceDeletionReport> ExecuteAsync(
        GameInstallation installation,
        InstanceDeletionPlan plan,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<DeletionStepResult>();
        var profile = GetProfileDirectory(installation);
        var folder = Path.GetFullPath(installation.GameDirectory);

        if (IsInstanceRunning(installation))
            return new InstanceDeletionReport(
                [new DeletionStepResult("运行状态", false, "实例仍在运行，未执行任何删除。")],
                Aborted: true);

        // 1. Archive saves. A failure here aborts before any metadata changes.
        if (plan.Disposition is SaveDisposition.Copy or SaveDisposition.Move)
        {
            if (string.IsNullOrWhiteSpace(plan.TargetDirectory))
            {
                return new InstanceDeletionReport(
                    [new DeletionStepResult("存档处理", false, "未指定存档目标目录。")],
                    Aborted: true);
            }

            try
            {
                var target = Path.GetFullPath(plan.TargetDirectory);
                if (IsUnder(target, profile) || IsUnder(profile, target))
                    throw new InvalidOperationException("存档目标不能位于实例存档目录内，也不能是它的上级目录。");

                if (plan.Disposition == SaveDisposition.Copy)
                {
                    CopySaves(profile, target);
                    steps.Add(new DeletionStepResult("存档复制", true, $"已复制到 {target}"));
                }
                else
                {
                    if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                        throw new IOException("目标目录已存在且不为空；请换一个目录。");
                    if (Directory.Exists(profile))
                    {
                        if (Directory.Exists(target)) Directory.Delete(target, recursive: false);
                        Directory.Move(profile, target);
                    }
                    else
                    {
                        Directory.CreateDirectory(target);
                    }

                    steps.Add(new DeletionStepResult("存档移动", true, $"已移动到 {target}"));
                }
            }
            catch (Exception exception)
            {
                _logger?.Warn($"实例存档处理失败：{exception.Message}");
                steps.Add(new DeletionStepResult("存档处理", false, exception.Message));
                return new InstanceDeletionReport(steps, Aborted: true);
            }
        }
        else
        {
            steps.Add(new DeletionStepResult("存档处理", true, "保持原位，未改动存档目录。"));
        }

        // 2. Optional game folder. Also before records, so a locked folder keeps
        // the instance usable instead of leaving a half-deleted record.
        if (plan.DeleteInstanceFolder)
        {
            try
            {
                if (IsInstanceRunning(installation))
                    throw new IOException("实例仍在运行。");

                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                    steps.Add(new DeletionStepResult("删除实例文件夹", true, folder));
                }
                else
                {
                    steps.Add(new DeletionStepResult("删除实例文件夹", true, "目录已不存在。"));
                }
            }
            catch (Exception exception)
            {
                _logger?.Warn($"删除实例文件夹失败：{exception.Message}");
                steps.Add(new DeletionStepResult("删除实例文件夹", false, exception.Message));
                return new InstanceDeletionReport(steps, Aborted: true);
            }
        }
        else
        {
            steps.Add(new DeletionStepResult("实例文件夹", true, "保留原目录，仅从 MK 中移除记录。"));
        }

        // 3. Metadata. Each is independent; failures are reported, not hidden.
        try
        {
            _instances.RemoveAlias(folder);
            steps.Add(new DeletionStepResult("移除版本记录", true, installation.DisplayLabel));
        }
        catch (Exception exception)
        {
            steps.Add(new DeletionStepResult("移除版本记录", false, exception.Message));
        }

        try
        {
            _groups.RemoveAll(folder);
            steps.Add(new DeletionStepResult("移除 Mod 组快照", true, "按实例隔离的组已删除。"));
        }
        catch (Exception exception)
        {
            steps.Add(new DeletionStepResult("移除 Mod 组快照", false, exception.Message));
        }

        try
        {
            var removed = _manualMatches.RemoveAll(folder);
            steps.Add(new DeletionStepResult("移除 Nexus 手动关联", true, $"删除 {removed} 条。"));
        }
        catch (Exception exception)
        {
            steps.Add(new DeletionStepResult("移除 Nexus 手动关联", false, exception.Message));
        }

        try
        {
            var removed = await _sources.RemoveAllAsync(folder, cancellationToken);
            steps.Add(new DeletionStepResult("移除安装来源记录", true, $"删除 {removed} 条。"));
        }
        catch (Exception exception)
        {
            steps.Add(new DeletionStepResult("移除安装来源记录", false, exception.Message));
        }

        // 4. Managed save profile, unless the user chose to keep it in place.
        if (plan.Disposition != SaveDisposition.Keep)
        {
            DetachLiveJunction(profile, steps);

            try
            {
                if (Directory.Exists(profile))
                {
                    Directory.Delete(profile, recursive: true);
                    steps.Add(new DeletionStepResult("删除托管存档目录", true, profile));
                }
                else
                {
                    steps.Add(new DeletionStepResult("删除托管存档目录", true, "目录已不存在。"));
                }
            }
            catch (Exception exception)
            {
                steps.Add(new DeletionStepResult("删除托管存档目录", false, exception.Message));
            }
        }

        return new InstanceDeletionReport(steps, Aborted: false);
    }

    /// <summary>
    /// If the game's live save directory is a junction into the profile we are
    /// about to delete, remove the link first so the game never writes through
    /// a dangling junction. The next MK launch recreates it for the new
    /// selection.
    /// </summary>
    private void DetachLiveJunction(string profile, ICollection<DeletionStepResult> steps)
    {
        if (!OperatingSystem.IsWindows()) return;

        var live = _saveIsolation.GetLiveSaveDirectory();
        try
        {
            if (!Directory.Exists(live)) return;
            var info = new DirectoryInfo(live);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;

            var target = info.LinkTarget;
            if (string.IsNullOrWhiteSpace(target)) return;
            if (!PathEquals(Path.GetFullPath(target), profile)) return;

            Directory.Delete(live, recursive: false);
            steps.Add(new DeletionStepResult("移除存档链接", true, "游戏存档链接已断开，下次启动时会重新指向当前版本。"));
        }
        catch (Exception exception)
        {
            steps.Add(new DeletionStepResult("移除存档链接", false, exception.Message));
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void CopySaves(string profile, string target)
    {
        if (!Directory.Exists(profile))
        {
            Directory.CreateDirectory(target);
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(profile, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(profile, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), ".mk-save-profile", StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(profile, file);
            File.Copy(file, Path.Combine(target, relative), overwrite: false);
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootFull = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
