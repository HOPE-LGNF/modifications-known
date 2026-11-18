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
            var blocked = RunningBlock(
                installation,
                () => "游戏正在运行；请先关闭游戏再导入存档。",
                () => "无法确认游戏是否正在运行（进程信息不可读）；为避免损坏存档，未导入。");
            if (blocked is not null) return Fail(blocked);

            var live = _options.LiveSaveDirectory;
            if (!Directory.Exists(live))
                return Fail("现有存档目录不存在，没有可导入的内容。");

            var staging = NewStagingDirectory(out var groupId);
            string? backup = null;
            string? previousTarget = null;
            var hadLink = false;
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

                if (_options.Linker.Exists(live) && _options.Linker.TryGetTarget(live, out var currentTarget))
                {
                    hadLink = true;
                    previousTarget = currentTarget;
                    var switched = SwitchLiveLink(group, live);
                    if (!switched.Success)
                    {
                        // The group holds a copy of the player's saves. It stays
                        // even when the switch failed, exactly like a failed ZIP
                        // import: it may be the only readable copy.
                        _options.Logger?.Warn($"导入后切换链接失败：{switched.Message}");
                        return new SaveGroupOperationResult(
                            false,
                            $"导入后切换链接失败，导入的副本「{group.Name}」已保留在 {group.DirectoryPath}；{switched.Message}" +
                            (switched.PreviousStateIntact
                                ? string.Empty
                                : " 当前存档链接状态未确认，请手动检查后再操作。"),
                            group);
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
                        _options.Logger?.Warn($"导入存档后创建隔离链接失败：{linkError}");
                        return new SaveGroupOperationResult(
                            false,
                            restore.Success
                                ? $"导入存档后创建隔离链接失败，原目录已恢复；导入的副本「{group.Name}」保留在 {group.DirectoryPath}：{linkError}"
                                : $"创建隔离链接失败（{linkError}），且原目录未能恢复到 {live}（{restore.Error}）；" +
                                  $"原目录仍在 {backup}，导入的副本「{group.Name}」保留在 {group.DirectoryPath}，请手动处理。",
                            group);
                    }
                }

                // Success means all three agree: the group is saved, the live
                // link points at it, and the index records it as the choice.
                if (!TryPersistSelection(installation, group.Id, out var persistError))
                {
                    var undo = UndoSwitch(live, hadLink, previousTarget, backup);
                    var kept = $"导入的副本「{group.Name}」保留在 {group.DirectoryPath}";
                    _options.Logger?.Warn($"导入现有存档后未能保存索引（{undo.Description}）：{persistError}");
                    return new SaveGroupOperationResult(
                        false,
                        undo.Success
                            ? $"导入的存档未能写入存档索引，已恢复原存档状态；{kept}。{undo.Description} 原因：{persistError}"
                            : $"导入的存档未能写入存档索引（{persistError}），且未能恢复原存档状态。" +
                              $"{undo.Description}；{kept}，请手动检查 {live} 后再操作。",
                        group);
                }

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
                var liveCheck = group is null
                    ? LiveLinkCheck.NotPointingAtGroup
                    : CheckLiveLink(live, group.DirectoryPath);
                _options.Logger?.Warn($"导入现有存档失败：{exception.Message}");
                if (group is null)
                {
                    return Fail(restored
                        ? $"导入现有存档失败，未改动原目录：{exception.Message}"
                        : $"导入现有存档失败：{exception.Message}；原目录仍在 {backup}，请手动检查。");
                }

                // The exception can arrive after the switch already happened, so
                // the report is derived from the link, not from an assumption.
                var note = liveCheck switch
                {
                    LiveLinkCheck.PointsAtGroup => "当前存档链接已指向导入的副本",
                    LiveLinkCheck.Unknown => "当前存档链接状态未确认，请手动检查",
                    _ => restored ? "原目录未改动" : $"原目录仍在 {backup}"
                };

                return new SaveGroupOperationResult(
                    false,
                    $"导入现有存档失败：{exception.Message}；{note}；" +
                    $"导入的副本「{group.Name}」保留在 {group.DirectoryPath}。",
                    group);
            }
        }
    }

    private string ResolveImportedGroupName(GameInstallation installation, string? requested)
    {
        // A requested name goes through the same policy as a suggested one, so
        // this path can never create a name that validation would reject.
        var baseName = SaveGroupFormat.SanitizeName(requested);
        if (baseName.Length == 0) baseName = "导入的存档";
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

    /// <summary>
    /// Records an imported group as the next-launch choice. A missing index
    /// entry and a failed write are both errors: reporting success while the
    /// index still names another group is a lie the next launch acts on.
    /// </summary>
    private bool TryPersistSelection(GameInstallation installation, Guid groupId, out string error)
    {
        try
        {
            var catalog = _catalog.Load();
            var entry = FindCatalogEntry(catalog, installation);
            if (entry is null)
            {
                _options.Logger?.Warn("导入后保存存档索引失败：索引中缺少该实例的记录。");
                error = "存档索引中缺少该实例的记录。";
                return false;
            }

            entry.NextLaunchGroupId = groupId;
            _catalog.Save(catalog);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"导入后保存存档索引失败：{exception.Message}");
            error = $"保存存档索引失败：{exception.Message}";
            return false;
        }
    }

    private sealed record LiveUndoResult(bool Success, string Description);

    /// <summary>
    /// Puts the live path back the way it was before a completed switch. Only a
    /// <c>Success</c> result may be reported as “已恢复”.
    /// </summary>
    private LiveUndoResult UndoSwitch(string live, bool hadLink, string? previousTarget, string? backup)
    {
        if (hadLink)
        {
            var restore = RestoreLiveLink(live, previousTarget);
            var expected = previousTarget ?? "（原本没有链接）";
            return new LiveUndoResult(
                restore.Success,
                restore.Success
                    ? $"已恢复原链接：{expected}"
                    : $"原链接未恢复（{restore.Stage}失败：{restore.Error}），请手动检查 {live}，预期指向 {expected}");
        }

        if (backup is null) return new LiveUndoResult(true, "原存档目录未曾移动");

        if (_options.Linker.Exists(live)
            && !_options.Linker.RemoveJunction(live, out var removeError))
        {
            return new LiveUndoResult(
                false,
                $"无法移除刚刚创建的存档链接（{removeError}）；原目录仍在 {backup}");
        }

        var restored = RestoreBackup(backup, live);
        return new LiveUndoResult(
            restored.Success,
            restored.Success
                ? $"原存档目录已恢复到 {live}"
                : $"原存档目录未能恢复到 {live}（{restored.Error}），仍保留在 {backup}");
    }

    private enum LiveLinkCheck
    {
        /// <summary>No link, or a link to a different directory.</summary>
        NotPointingAtGroup,
        PointsAtGroup,
        Unknown
    }

    /// <summary>Reads where the live path points without changing anything.</summary>
    private LiveLinkCheck CheckLiveLink(string live, string groupDirectory)
    {
        try
        {
            if (!_options.Linker.Exists(live)) return LiveLinkCheck.NotPointingAtGroup;
            if (!_options.Linker.TryGetTarget(live, out var target))
                return IsReparsePoint(live) ? LiveLinkCheck.Unknown : LiveLinkCheck.NotPointingAtGroup;

            return PathEquals(target, groupDirectory)
                ? LiveLinkCheck.PointsAtGroup
                : LiveLinkCheck.NotPointingAtGroup;
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"读取存档链接状态失败：{exception.Message}");
            return LiveLinkCheck.Unknown;
        }
    }

    /// <summary>An empty group may only be discarded once the live link is
    /// confirmed not to point at it; otherwise deleting it would leave the game
    /// pointed at a missing directory.</summary>
    private bool CanDiscardGroup(string live, SaveGroup group) =>
        CheckLiveLink(live, group.DirectoryPath) == LiveLinkCheck.NotPointingAtGroup;

    /// <summary>Puts back an empty real folder that a switch removed before it
    /// failed, so “unchanged” stays literally true.</summary>
    private void TryRecreateEmptyDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"恢复空的存档目录失败：{exception.Message}");
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

        var live = _options.LiveSaveDirectory;
        switch (CheckLiveLink(live, group.DirectoryPath))
        {
            case LiveLinkCheck.PointsAtGroup:
                return Fail("这是正在使用的存档组；请先切换到其他组，再删除。");
            case LiveLinkCheck.Unknown:
                // A link that exists but cannot be read could point anywhere,
                // including at the group about to be recycled: refuse.
                return Fail(
                    $"无法确认当前存档链接的目标（{live}）；为避免误删，未删除。" +
                    "请手动检查该链接后再试。");
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
            var entry = FindCatalogEntry(catalog, installation);
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

    private enum LinkSwitchOutcome
    {
        /// <summary>The live link now points at the requested group.</summary>
        Switched,

        /// <summary>The live link already pointed at the requested group.</summary>
        AlreadyCurrent,

        /// <summary>The switch failed and the previous live state is confirmed
        /// to be intact (either untouched or restored and verified).</summary>
        FailedUnchanged,

        /// <summary>The switch failed and the live state could not be
        /// confirmed; callers must keep data and report a partial failure.</summary>
        FailedUnknown
    }

    private sealed record LinkSwitchResult(
        LinkSwitchOutcome Outcome,
        string Message,
        string? PreviousTarget)
    {
        public bool Success =>
            Outcome is LinkSwitchOutcome.Switched or LinkSwitchOutcome.AlreadyCurrent;

        public bool Switched => Outcome == LinkSwitchOutcome.Switched;

        /// <summary>True only when a failed switch left the live path exactly
        /// as it was; false means the state is unknown and destructive
        /// rollbacks must not run.</summary>
        public bool PreviousStateIntact => Outcome == LinkSwitchOutcome.FailedUnchanged;
    }

    private static LinkSwitchResult FailedUnchanged(string message, string? previousTarget) =>
        new(LinkSwitchOutcome.FailedUnchanged, message, previousTarget);

    private static LinkSwitchResult FailedUnknown(string message, string? previousTarget) =>
        new(LinkSwitchOutcome.FailedUnknown, message, previousTarget);

    /// <summary>
    /// Shared junction switch used by apply and launch. It never overwrites a
    /// real directory with data or an external link, and restores the previous
    /// target if creating the new link fails. The result says whether that
    /// restore was confirmed, so callers can decide between a safe rollback and
    /// keeping data that the live link might still reference.
    /// </summary>
    private LinkSwitchResult SwitchLiveLink(SaveGroup group, string live)
    {
        var touched = false;
        var removedEmptyDirectory = false;
        try
        {
            if (_options.Linker.Exists(live))
            {
                if (_options.Linker.TryGetTarget(live, out var currentTarget))
                {
                    var current = Path.GetFullPath(currentTarget);
                    if (!IsUnder(current, ProfilesRoot))
                    {
                        return FailedUnchanged(
                            $"现有存档链接指向 MK 之外的位置：{current}。未切换。",
                            current);
                    }

                    if (PathEquals(current, group.DirectoryPath))
                    {
                        return new LinkSwitchResult(
                            LinkSwitchOutcome.AlreadyCurrent,
                            $"存档已指向「{group.Name}」。",
                            current);
                    }

                    if (!_options.Linker.RemoveJunction(live, out var removeError))
                    {
                        return FailedUnchanged(
                            $"切换存档组失败：无法移除现有链接（{removeError}）。",
                            current);
                    }

                    touched = true;
                    if (!_options.Linker.CreateJunction(live, group.DirectoryPath, out var createError))
                    {
                        var restore = RestoreLiveLink(live, current);
                        return restore.Success
                            ? FailedUnchanged(
                                $"切换存档组失败：{createError}。已恢复原链接：{current}",
                                current)
                            : FailedUnknown(
                                $"切换存档组失败：{createError}。原链接未恢复（{restore.Stage}失败：{restore.Error}），" +
                                $"请手动检查 {live}，预期指向 {current}。",
                                current);
                    }

                    return new LinkSwitchResult(
                        LinkSwitchOutcome.Switched,
                        $"存档已切换到「{group.Name}」。",
                        current);
                }

                if (Directory.EnumerateFileSystemEntries(live).Any())
                {
                    return FailedUnchanged(
                        $"检测到未托管的存档目录：{live}。请先导入或处理，未切换。",
                        null);
                }

                Directory.Delete(live, recursive: false);
                touched = true;
                removedEmptyDirectory = true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(live)!);
            if (!_options.Linker.CreateJunction(live, group.DirectoryPath, out var firstLinkError))
            {
                // Nothing pointed at the candidate before, so the only question
                // is whether the failed create left a usable link behind. An
                // empty real folder is put back so "unchanged" is literally true.
                if (removedEmptyDirectory) TryRecreateEmptyDirectory(live);

                return CheckLiveLink(live, group.DirectoryPath) == LiveLinkCheck.NotPointingAtGroup
                    ? FailedUnchanged($"创建存档链接失败：{firstLinkError}。", null)
                    : FailedUnknown(
                        $"创建存档链接失败：{firstLinkError}；无法确认 {live} 的当前状态，请手动检查。",
                        null);
            }

            return new LinkSwitchResult(
                LinkSwitchOutcome.Switched,
                $"存档已指向「{group.Name}」。",
                null);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"切换存档组失败：{exception.Message}");
            var message = $"切换存档组失败：{exception.Message}。";
            if (!touched && CheckLiveLink(live, group.DirectoryPath) == LiveLinkCheck.NotPointingAtGroup)
                return FailedUnchanged(message, null);

            return FailedUnknown(message + $" 无法确认 {live} 的当前状态，请手动检查。", null);
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
                // The migrated copy holds the player's data; it stays even when
                // the switch failed, and the legacy directory is untouched.
                _options.Logger?.Warn($"迁移旧存档后切换链接失败（迁移副本保留）：{applied.Message}");
                return Failed(
                    $"已把旧存档复制为「{group.Name}」，但未能应用为当前存档：{applied.Message}" +
                    $"原目录未改动（{sourceDirectory}）；迁移副本保留在 {group.DirectoryPath}。" +
                    (applied.PreviousStateIntact ? string.Empty : " 当前存档链接状态未确认，请手动检查后再操作。"));
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
                // The imported copy holds the player's saves; keep it in both
                // cases instead of cleaning it up like an empty group.
                return Failed(restore.Success
                    ? $"导入存档后创建隔离链接失败，原目录已恢复；导入的副本「{group.Name}」保留在 {group.DirectoryPath}：{linkError}"
                    : $"创建隔离链接失败（{linkError}），且原目录未能恢复到 {live}（{restore.Error}）；" +
                      $"原目录仍在 {backup}，导入的副本「{group.Name}」保留在 {group.DirectoryPath}，请手动处理。");
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
            var restored = backup is null || RestoreBackup(backup, live).Success;
            _options.Logger?.Warn($"导入未托管存档失败：{exception.Message}");
            if (group is null)
            {
                return Failed(restored
                    ? $"导入未托管存档失败，原存档保持不变：{exception.Message}"
                    : $"导入未托管存档失败：{exception.Message}；原目录未能恢复到 {live}，仍保留在 {backup}。");
            }

            return Failed(restored
                ? $"导入未托管存档失败，原存档保持不变：{exception.Message}；导入的副本「{group.Name}」保留在 {group.DirectoryPath}。"
                : $"导入未托管存档失败：{exception.Message}；原目录未能恢复到 {live}，仍保留在 {backup}。" +
                  $"导入的副本「{group.Name}」保留在 {group.DirectoryPath}，请手动检查后再操作。");
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
                if (restore.Success && CanDiscardGroup(live, group))
                    RollbackGroup(installation, group);
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
            var restored = backup is null || RestoreBackup(backup, live).Success;
            if (group is not null && CanDiscardGroup(live, group))
                RollbackGroup(installation, group);
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

            var live = _options.LiveSaveDirectory;
            var applied = SwitchLiveLink(group, live);
            if (!applied.Success)
            {
                // Creating the first empty group is one logical operation: when
                // the switch failed and the old live state is confirmed, the
                // empty group is discarded. Otherwise it stays, because the live
                // link might still point at it.
                if (applied.PreviousStateIntact && CanDiscardGroup(live, group))
                {
                    RollbackGroup(installation, group);
                    _options.Logger?.Warn($"创建默认存档组后切换链接失败：{applied.Message}");
                    return Failed($"创建默认存档组后切换链接失败，已回滚：{applied.Message}");
                }

                _options.Logger?.Warn($"创建默认存档组后切换链接失败（新组保留）：{applied.Message}");
                return Failed(
                    $"已创建默认存档组「{group.Name}」，但未能应用为当前存档：{applied.Message}" +
                    $"该组保留在 {group.DirectoryPath}；请手动检查当前存档链接后再决定是否删除。");
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
