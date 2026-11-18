using System.Diagnostics;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Instances;

/// <summary>
/// The only choice left when removing an instance record: whether the game
/// install folder goes away too. Save groups are never part of the plan.
/// </summary>
public sealed record InstanceDeletionPlan(bool DeleteInstanceFolder);

public sealed record DeletionStepResult(string Name, bool Success, string Message);

public sealed record InstanceDeletionReport(IReadOnlyList<DeletionStepResult> Steps, bool Aborted)
{
    public bool Success => !Aborted && Steps.All(step => step.Success);

    public string Summary
    {
        get
        {
            var failed = Steps.Count(step => !step.Success);
            if (Aborted) return "删除已中止；没有删除实例记录，存档组未改动。";
            return failed == 0
                ? "实例已删除；全部存档组已保留。"
                : $"实例删除完成，但有 {failed} 个步骤失败（见下表）；存档组未改动。";
        }
    }
}

/// <summary>
/// Removes one MK instance record. Save groups are deliberately out of scope:
/// the service never copies, moves, deletes or re-links a group, never touches
/// the live LocalLow junction and never edits <c>save-groups.json</c>. The only
/// optional destructive step is the game install folder, and it runs before any
/// record so a locked folder leaves a usable instance instead of a half-deleted
/// one. Removing metadata is per item: a failure is reported and the remaining
/// items still run, with no rollback of the items that already succeeded.
/// </summary>
public sealed class InstanceDeletionService
{
    private const string RunningStep = "运行状态";
    private const string GameFolderStep = "删除游戏安装目录";
    private const string InstanceRecordStep = "移除版本记录";
    private const string ModGroupStep = "移除 Mod 组快照";
    private const string ManualMatchStep = "移除 Nexus 手动关联";
    private const string SourceStep = "移除安装来源记录";
    private const string SaveGroupStep = "存档组";

    private readonly GameInstanceStore _instances;
    private readonly Func<string, bool> _removeModGroups;
    private readonly Func<string, int> _removeManualMatches;
    private readonly IInstalledSourceStore _sources;
    private readonly SaveGroupService _saveGroups;
    private readonly Func<GameInstallation, bool> _isRunning;
    private readonly IAppLogger? _logger;

    public InstanceDeletionService(
        GameInstanceStore instances,
        Func<string, bool> removeModGroups,
        Func<string, int> removeManualMatches,
        IInstalledSourceStore sources,
        SaveGroupService? saveGroups = null,
        Func<GameInstallation, bool>? isRunning = null,
        IAppLogger? logger = null)
    {
        _instances = instances;
        _removeModGroups = removeModGroups;
        _removeManualMatches = removeManualMatches;
        _sources = sources;
        _saveGroups = saveGroups ?? new SaveGroupService();
        _isRunning = isRunning ?? IsInstanceRunning;
        _logger = logger;
    }

    /// <summary>
    /// Read-only hand-off for the confirmation dialog: it only lists what will
    /// be kept. Deleting an instance never creates, renames, selects or removes
    /// a group.
    /// </summary>
    public IReadOnlyList<SaveGroup> GetGroups(GameInstallation installation) =>
        _saveGroups.GetGroups(installation);

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
        var folder = Path.GetFullPath(installation.GameDirectory);

        // A running game keeps writing into the live profile; in that state
        // nothing is changed — not even the record.
        if (_isRunning(installation))
            return new InstanceDeletionReport(
                [new DeletionStepResult(RunningStep, false, "实例正在运行，未执行任何删除。")],
                Aborted: true);

        // 1. Optional game folder. It runs before any record so a locked folder
        //    aborts with the instance still usable.
        var (folderDeleted, folderMessage) = plan.DeleteInstanceFolder
            ? TryDeleteGameFolder(folder)
            : (true, "保留游戏安装目录，仅从 MK 中移除记录。");

        steps.Add(new DeletionStepResult(GameFolderStep, folderDeleted, folderMessage));
        if (!folderDeleted) return new InstanceDeletionReport(steps, Aborted: true);

        // 2. Metadata. Each item is independent: one failure is reported and the
        //    others still run. Nothing here can reach a save group.
        try
        {
            var removed = _instances.Remove(folder);
            steps.Add(new DeletionStepResult(
                InstanceRecordStep,
                true,
                removed ? installation.DisplayLabel : "实例记录已不存在。"));
        }
        catch (Exception exception)
        {
            _logger?.Warn($"移除实例记录失败：{exception.Message}");
            steps.Add(new DeletionStepResult(InstanceRecordStep, false, exception.Message));
        }

        try
        {
            _removeModGroups(folder);
            steps.Add(new DeletionStepResult(ModGroupStep, true, "按实例隔离的 Mod 组已删除。"));
        }
        catch (Exception exception)
        {
            _logger?.Warn($"移除 Mod 组快照失败：{exception.Message}");
            steps.Add(new DeletionStepResult(ModGroupStep, false, exception.Message));
        }

        try
        {
            var removed = _removeManualMatches(folder);
            steps.Add(new DeletionStepResult(ManualMatchStep, true, $"删除 {removed} 条。"));
        }
        catch (Exception exception)
        {
            _logger?.Warn($"移除 Nexus 手动关联失败：{exception.Message}");
            steps.Add(new DeletionStepResult(ManualMatchStep, false, exception.Message));
        }

        try
        {
            var removed = await _sources.RemoveAllAsync(folder, cancellationToken);
            steps.Add(new DeletionStepResult(SourceStep, true, $"删除 {removed} 条。"));
        }
        catch (Exception exception)
        {
            _logger?.Warn($"移除安装来源记录失败：{exception.Message}");
            steps.Add(new DeletionStepResult(SourceStep, false, exception.Message));
        }

        // 3. Say out loud what was deliberately not touched: the user must know
        //    the groups are still sitting in the MK data directory.
        steps.Add(new DeletionStepResult(
            SaveGroupStep,
            true,
            "全部存档组仍保留在 MK 数据目录；未复制、移动、删除或重新链接。"));

        return new InstanceDeletionReport(steps, Aborted: false);
    }

    private (bool Success, string Message) TryDeleteGameFolder(string folder)
    {
        try
        {
            // Deleting the install folder must never reach into the player's
            // AppData (instance records, save groups, settings or LocalLow).
            if (OverlapsAppData(folder))
                throw new InvalidOperationException(
                    "游戏安装目录与 AppData 数据目录重叠；已拒绝删除，避免丢失存档组。");

            if (!Directory.Exists(folder)) return (true, "目录已不存在。");

            Directory.Delete(folder, recursive: true);
            return (true, folder);
        }
        catch (Exception exception)
        {
            _logger?.Warn($"删除游戏安装目录失败：{exception.Message}");
            return (false, exception.Message);
        }
    }

    private static bool OverlapsAppData(string folder) =>
        AppDataRoots().Any(root =>
            !string.IsNullOrWhiteSpace(root) && (IsUnder(folder, root) || IsUnder(root, folder)));

    private static IEnumerable<string> AppDataRoots()
    {
        yield return AppPaths.DefaultRootDirectory;
        yield return AppPaths.RootDirectory;
        yield return AppPaths.SaveProfilesDirectory;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            yield return Path.Combine(userProfile, "AppData", "LocalLow");
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rootFull.Length == 0) return false;

        return full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
