using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Saves;

public sealed partial class SaveGroupService
{
    /// <summary>
    /// Copies whatever the live directory currently holds into a new group of
    /// this installation and makes it the live one. Used when an installation
    /// that already has groups adopts unmanaged saves. The original directory is
    /// kept as a backup when it is a real folder rather than a link.
    /// </summary>
    public Task<SaveGroupOperationResult> ImportLiveIntoNewGroupAsync(
        GameInstallation installation,
        string? name = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportLiveIntoNewGroupCore(installation, name), cancellationToken);

    private SaveGroupOperationResult ImportLiveIntoNewGroupCore(
        GameInstallation installation,
        string? name)
    {
        lock (_writeGate)
        {
            var running = ProbeRunningState(installation);
            if (running == GameRunningState.Running)
                return Fail("游戏正在运行；请先关闭游戏再导入存档。");
            if (running == GameRunningState.Unknown)
                return Fail("无法确认游戏是否正在运行（进程信息不可读）；为避免损坏存档，未导入。");

            var live = _options.LiveSaveDirectory;
            if (!Directory.Exists(live))
                return Fail("现有存档目录不存在，没有可导入的内容。");

            var staging = NewStagingDirectory(out var groupId);
            string? backup = null;
            SaveGroup? group = null;

            try
            {
                var files = CollectDataFiles(live);
                if (files.Count == 0)
                {
                    DeleteDirectoryQuietly(staging);
                    return Fail("现有存档目录是空的，没有可导入的文件。");
                }

                CopyFiles(live, staging, files);
                VerifyCopy(live, staging, files);

                group = CommitGroup(
                    installation,
                    staging,
                    groupId,
                    ResolveImportedGroupName(installation, name),
                    isDefault: false,
                    _options.UtcNow());

                if (_options.Linker.Exists(live) && _options.Linker.TryGetTarget(live, out _))
                {
                    var switched = SwitchLiveLink(group, live);
                    if (!switched.Success)
                    {
                        RollbackGroup(installation, group);
                        return Fail($"导入后切换链接失败，新组已回滚：{switched.Message}");
                    }
                }
                else
                {
                    // A real folder with saves: move it aside first so creating the
                    // junction cannot destroy the original data.
                    backup = MoveLiveToBackup(live);
                    if (!_options.Linker.CreateJunction(live, group.DirectoryPath, out var linkError))
                    {
                        var restore = RestoreBackup(backup, live);
                        if (restore.Success) RollbackGroup(installation, group);
                        return Fail(restore.Success
                            ? $"导入存档后创建隔离链接失败，原目录已恢复：{linkError}"
                            : $"创建隔离链接失败（{linkError}），原目录仍在 {backup}；" +
                              $"导入的存档组「{group.Name}」已保留，请手动处理。");
                    }
                }

                TrySelectGroup(installation, group.Id);
                _options.Logger?.Info($"已把现有存档导入为「{group.Name}」（{group.DirectoryPath}）");
                return new SaveGroupOperationResult(
                    true,
                    backup is null
                        ? $"已把现有存档导入为「{group.Name}」，并切换为当前存档。"
                        : $"已把现有存档导入为「{group.Name}」并切换为当前存档；原目录备份在 {backup}。",
                    group);
            }
            catch (Exception exception)
            {
                DeleteDirectoryQuietly(staging);
                var restored = backup is null || RestoreBackup(backup, live).Success;
                if (group is not null && restored) RollbackGroup(installation, group);
                _options.Logger?.Warn($"导入现有存档失败：{exception.Message}");
                return Fail(restored
                    ? $"导入现有存档失败，未改动原目录：{exception.Message}"
                    : $"导入现有存档失败：{exception.Message}；原目录仍在 {backup}，请手动检查。");
            }
        }
    }

    private string ResolveImportedGroupName(GameInstallation installation, string? requested)
    {
        var baseName = string.IsNullOrWhiteSpace(requested) ? "导入的存档" : requested.Trim();
        var existing = Reconcile(installation, persist: false).Groups
            .Select(group => group.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName)) return baseName;

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseName} {index}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Records an imported group as the next-launch choice. The link is
    /// already switched; a catalog failure is logged rather than rolled back.</summary>
    private void TrySelectGroup(GameInstallation installation, Guid groupId)
    {
        try
        {
            var catalog = _catalog.Load();
            var entry = catalog.Installations.FirstOrDefault(candidate =>
                string.Equals(candidate.InstallationKey, GetInstallationKey(installation),
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;

            entry.NextLaunchGroupId = groupId;
            _catalog.Save(catalog);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"导入后保存存档索引失败：{exception.Message}");
        }
    }

    private SaveGroupOperationResult DeleteGroupCore(GameInstallation installation, Guid groupId) =>
        Mutate(installation, "删除存档组", () => DeleteGroupBody(installation, groupId), Fail);

    private SaveGroupOperationResult DeleteGroupBody(GameInstallation installation, Guid groupId)
    {
        var reconciled = Reconcile(installation, persist: true);
        var groups = reconciled.Groups;
        var group = groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null)
            return Fail("要删除的存档组不存在，或不属于当前游戏目录。");

        if (groups.Count <= 1)
            return Fail("至少保留一个存档组；这是最后一个组，不能删除。");

        if (reconciled.Entry?.NextLaunchGroupId == groupId)
            return Fail("这是该实例选定的存档组；请先选定另一个组，再删除。");

        if (_options.Linker.Exists(_options.LiveSaveDirectory))
        {
            if (_options.Linker.TryGetTarget(_options.LiveSaveDirectory, out var liveTarget))
            {
                if (PathEquals(liveTarget, group.DirectoryPath))
                    return Fail("这是正在使用的存档组；请先切换到其他组，再删除。");
            }
            else if (IsReparsePoint(_options.LiveSaveDirectory))
            {
                // A link that exists but cannot be read could point anywhere,
                // including at the group about to be recycled: refuse.
                return Fail(
                    $"无法确认当前存档链接的目标（{_options.LiveSaveDirectory}）；为避免误删，未删除。" +
                    "请手动检查该链接后再试。");
            }
        }

        if (!Directory.Exists(group.DirectoryPath))
            return Fail($"存档组目录不存在：{group.DirectoryPath}");

        if (!_options.RecycleBin.TrySendDirectory(group.DirectoryPath, out var recycleError))
        {
            _options.Logger?.Warn($"删除存档组失败（回收站）：{recycleError}");
            return Fail($"删除存档组失败，未做任何修改：{recycleError}");
        }

        try
        {
            var catalog = _catalog.Load();
            var entry = catalog.Installations.FirstOrDefault(candidate =>
                string.Equals(candidate.InstallationKey, GetInstallationKey(installation),
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                throw new InvalidOperationException("存档组索引缺失。");

            entry.Groups.RemoveAll(record => record.Id == groupId);
            if (entry.NextLaunchGroupId == groupId)
                entry.NextLaunchGroupId = entry.Groups.OrderBy(record => record.CreatedAt).FirstOrDefault()?.Id;
            _catalog.Save(catalog);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"存档组已进回收站，但索引更新失败：{exception.Message}");
            return Fail($"已移入 Windows 回收站，但索引更新失败；刷新后该组会从列表消失：{exception.Message}");
        }

        _options.Logger?.Info($"存档组已移入 Windows 回收站：{group.Name}（{group.DirectoryPath}）");
        return new SaveGroupOperationResult(
            true,
            $"已将「{group.Name}」移入 Windows 回收站；可从回收站还原。",
            group);
    }

    private SaveLaunchResult PrepareLaunchCore(GameInstallation installation) =>
        Mutate(installation, "切换存档组",
            () => PrepareLaunchBody(installation),
            message => new SaveLaunchResult(false, message));

    private SaveLaunchResult PrepareLaunchBody(GameInstallation installation)
    {
        if (!_options.Linker.IsSupported)
            return new SaveLaunchResult(true, "非 Windows 环境，跳过存档隔离。");

        var reconciled = Reconcile(installation, persist: true);
        var group = SelectGroup(reconciled.Groups, reconciled.Entry?.NextLaunchGroupId);
        if (group is null)
            return new SaveLaunchResult(false, "还没有可用的存档组；请先在存档管理中创建或迁移。");

        if (!Directory.Exists(group.DirectoryPath))
            return new SaveLaunchResult(false, $"存档组目录不存在：{group.DirectoryPath}", group);

        var switched = SwitchLiveLink(group, _options.LiveSaveDirectory);
        if (!switched.Success)
            return new SaveLaunchResult(false, $"{switched.Message} 游戏不会启动。", group);

        return new SaveLaunchResult(true, switched.Message, group, switched.Switched);
    }

    private sealed record LinkSwitchResult(
        bool Success,
        string Message,
        bool Switched,
        string? PreviousTarget);

    /// <summary>
    /// Shared junction switch used by apply and launch. It never overwrites a
    /// real directory with data or an external link, and restores the previous
    /// target if creating the new link fails.
    /// </summary>
    private LinkSwitchResult SwitchLiveLink(SaveGroup group, string live)
    {
        try
        {
            if (_options.Linker.Exists(live))
            {
                if (_options.Linker.TryGetTarget(live, out var currentTarget))
                {
                    var current = Path.GetFullPath(currentTarget);
                    if (!IsUnder(current, ProfilesRoot))
                    {
                        return new LinkSwitchResult(
                            false,
                            $"现有存档链接指向 MK 之外的位置：{current}。未切换。",
                            false,
                            current);
                    }

                    if (PathEquals(current, group.DirectoryPath))
                    {
                        return new LinkSwitchResult(
                            true,
                            $"存档已指向「{group.Name}」。",
                            false,
                            current);
                    }

                    if (!_options.Linker.RemoveJunction(live, out var removeError))
                    {
                        return new LinkSwitchResult(
                            false,
                            $"切换存档组失败：无法移除现有链接（{removeError}）。",
                            false,
                            current);
                    }

                    if (!_options.Linker.CreateJunction(live, group.DirectoryPath, out var createError))
                    {
                        var restore = RestoreLiveLink(live, current);
                        var recovery = restore.Success
                            ? $"已恢复原链接：{current}"
                            : $"原链接未恢复（{restore.Stage}失败：{restore.Error}），请手动检查 {live}，预期指向 {current}。";
                        return new LinkSwitchResult(
                            false,
                            $"切换存档组失败：{createError}。{recovery}",
                            false,
                            current);
                    }

                    return new LinkSwitchResult(
                        true,
                        $"存档已切换到「{group.Name}」。",
                        true,
                        current);
                }

                if (Directory.EnumerateFileSystemEntries(live).Any())
                {
                    return new LinkSwitchResult(
                        false,
                        $"检测到未托管的存档目录：{live}。请先导入或处理，未切换。",
                        false,
                        null);
                }

                Directory.Delete(live, recursive: false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(live)!);
            if (!_options.Linker.CreateJunction(live, group.DirectoryPath, out var firstLinkError))
            {
                return new LinkSwitchResult(
                    false,
                    $"创建存档链接失败：{firstLinkError}。",
                    false,
                    null);
            }

            return new LinkSwitchResult(
                true,
                $"存档已指向「{group.Name}」。",
                true,
                null);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"切换存档组失败：{exception.Message}");
            return new LinkSwitchResult(
                false,
                $"切换存档组失败：{exception.Message}。",
                false,
                null);
        }
    }

    private sealed record LinkRestoreResult(bool Success, string Stage, string? Error);

    /// <summary>
    /// Undoes a junction switch and reports exactly what happened. Callers must
    /// only claim "已恢复" when <c>Success</c> is true; a silent failure here
    /// would leave the game pointed at the wrong profile.
    /// </summary>
    private LinkRestoreResult RestoreLiveLink(string live, string? previousTarget)
    {
        try
        {
            if (!_options.Linker.RemoveJunction(live, out var removeError))
            {
                _options.Logger?.Warn($"恢复存档链接失败（移除当前链接）：{removeError}");
                return new LinkRestoreResult(false, "移除当前链接", removeError);
            }

            if (previousTarget is null) return new LinkRestoreResult(true, "移除当前链接", null);

            if (!_options.Linker.CreateJunction(live, previousTarget, out var createError))
            {
                _options.Logger?.Warn($"恢复存档链接失败（重建原链接）：{createError}");
                return new LinkRestoreResult(false, "重建原链接", createError);
            }

            return new LinkRestoreResult(true, "重建原链接", null);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"恢复存档链接失败：{exception.Message}");
            return new LinkRestoreResult(false, "恢复链接", exception.Message);
        }
    }

    private SaveGroupInitializationResult MigrateLegacy(
        GameInstallation installation,
        string sourceDirectory,
        string live)
    {
        var staging = NewStagingDirectory(out var groupId);
        try
        {
            var files = CollectDataFiles(sourceDirectory);
            CopyFiles(sourceDirectory, staging, files);
            VerifyCopy(sourceDirectory, staging, files);

            var now = _options.UtcNow();
            var group = CommitGroup(installation, staging, groupId, "默认", isDefault: true, now);
            _options.Logger?.Info(
                $"已把旧存档 profile 迁移到默认存档组：{sourceDirectory} -> {group.DirectoryPath}（源目录保留为回退副本）");

            var applied = SwitchLiveLink(group, live);
            if (!applied.Success)
            {
                RollbackGroup(installation, group);
                _options.Logger?.Warn($"迁移旧存档后切换链接失败：{applied.Message}");
                return Failed($"迁移旧存档后切换链接失败，新组已回滚；原存档未改动：{applied.Message}");
            }

            return Ready(
                $"已把旧存档迁移到默认存档组并应用为当前存档；原目录保留为回退副本（{sourceDirectory}）。",
                GroupListAfterCommit(installation),
                group.Id,
                live);
        }
        catch (Exception exception)
        {
            DeleteDirectoryQuietly(staging);
            _options.Logger?.Warn($"迁移旧存档失败：{exception.Message}");
            return Failed($"迁移旧存档失败，未切换链接、未启动：{exception.Message}");
        }
    }

    private SaveGroupInitializationResult ImportUnmanaged(GameInstallation installation, string live)
    {
        string? backup = null;
        SaveGroup? group = null;
        var staging = NewStagingDirectory(out var groupId);
        try
        {
            var files = CollectDataFiles(live);
            CopyFiles(live, staging, files);
            VerifyCopy(live, staging, files);

            group = CommitGroup(installation, staging, groupId, "默认", isDefault: true, _options.UtcNow());
            backup = MoveLiveToBackup(live);

            if (!_options.Linker.CreateJunction(live, group.DirectoryPath, out var linkError))
            {
                var restore = RestoreBackup(backup, live);
                if (restore.Success) RollbackGroup(installation, group);
                return Failed(restore.Success
                    ? $"导入存档后创建隔离链接失败，原目录已恢复：{linkError}"
                    : $"创建隔离链接失败（{linkError}），且原目录未能恢复到 {live}（{restore.Error}）；" +
                      $"原目录仍在 {backup}，导入的存档组「{group.Name}」已保留，请手动处理。");
            }

            return Ready(
                $"已导入现存存档到默认存档组；原目录备份在 {backup}。",
                GroupListAfterCommit(installation),
                group.Id,
                live);
        }
        catch (Exception exception)
        {
            DeleteDirectoryQuietly(staging);
            var restored = backup is null ? true : RestoreBackup(backup, live).Success;
            if (group is not null && restored) RollbackGroup(installation, group);
            _options.Logger?.Warn($"导入未托管存档失败：{exception.Message}");
            return Failed(restored
                ? $"导入未托管存档失败，原存档保持不变：{exception.Message}"
                : $"导入未托管存档失败：{exception.Message}；原目录未能恢复到 {live}，仍保留在 {backup}。" +
                  $"已复制到存档组的副本也已保留，请手动检查后再操作。");
        }
    }

    private SaveGroupInitializationResult CreateEmptyOverUnmanaged(GameInstallation installation, string live)
    {
        string? backup = null;
        SaveGroup? group = null;
        var staging = NewStagingDirectory(out var groupId);
        try
        {
            group = CommitGroup(installation, staging, groupId, "默认", isDefault: true, _options.UtcNow());
            backup = MoveLiveToBackup(live);

            if (!_options.Linker.CreateJunction(live, group.DirectoryPath, out var linkError))
            {
                var restore = RestoreBackup(backup, live);
                if (restore.Success) RollbackGroup(installation, group);
                return Failed(restore.Success
                    ? $"新建空组后创建隔离链接失败，原目录已恢复：{linkError}"
                    : $"创建隔离链接失败（{linkError}），且原目录未能恢复到 {live}（{restore.Error}）；" +
                      $"原存档目录仍在 {backup}，请手动处理。");
            }

            return Ready(
                $"已新建空的默认存档组；原存档目录备份在 {backup}，未删除。",
                GroupListAfterCommit(installation),
                group.Id,
                live);
        }
        catch (Exception exception)
        {
            DeleteDirectoryQuietly(staging);
            var restored = backup is null ? true : RestoreBackup(backup, live).Success;
            if (group is not null) RollbackGroup(installation, group);
            _options.Logger?.Warn($"新建空组失败：{exception.Message}");
            return Failed(restored
                ? $"新建空组失败，原存档保持不变：{exception.Message}"
                : $"新建空组失败：{exception.Message}；原存档目录未能恢复到 {live}，仍保留在 {backup}。");
        }
    }

    private SaveGroupInitializationResult CreateEmptyDefault(GameInstallation installation, string message)
    {
        var staging = NewStagingDirectory(out var groupId);
        try
        {
            var group = CommitGroup(installation, staging, groupId, "默认", isDefault: true, _options.UtcNow());

            var applied = SwitchLiveLink(group, _options.LiveSaveDirectory);
            if (!applied.Success)
            {
                RollbackGroup(installation, group);
                _options.Logger?.Warn($"创建默认存档组后切换链接失败：{applied.Message}");
                return Failed($"创建默认存档组后切换链接失败，已回滚：{applied.Message}");
            }

            return Ready(
                $"{message} 已应用为当前存档。",
                GroupListAfterCommit(installation),
                group.Id,
                _options.LiveSaveDirectory);
        }
        catch (Exception exception)
        {
            DeleteDirectoryQuietly(staging);
            _options.Logger?.Warn($"创建默认存档组失败：{exception.Message}");
            return Failed($"创建默认存档组失败：{exception.Message}");
        }
    }
}
