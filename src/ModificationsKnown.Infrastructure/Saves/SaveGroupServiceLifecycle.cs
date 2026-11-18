using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Saves;

public sealed partial class SaveGroupService
{
    /// <summary>
    /// Prepares the data model before a launch or before the saves page shows
    /// groups. It never overwrites unmanaged saves: when they exist the caller
    /// must pick <see cref="UnmanagedSaveAction"/> (or cancel, which changes
    /// nothing).
    /// </summary>
    public Task<SaveGroupInitializationResult> InitializeAsync(
        GameInstallation installation,
        UnmanagedSaveAction? unmanagedAction = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => InitializeCore(installation, unmanagedAction), cancellationToken);
    }

    private SaveGroupInitializationResult InitializeCore(
        GameInstallation installation,
        UnmanagedSaveAction? unmanagedAction) =>
        Mutate(installation, "初始化存档组", () => InitializeBody(installation, unmanagedAction), Failed);

    private SaveGroupInitializationResult InitializeBody(
        GameInstallation installation,
        UnmanagedSaveAction? unmanagedAction,
        bool allowTakeover = false)
    {
        if (!_options.Linker.IsSupported)
        {
            return Ready(
                "非 Windows 环境，跳过存档隔离。",
                [],
                null,
                "非 Windows 环境");
        }

        var reconciled = Reconcile(installation, persist: true);
        if (reconciled.Groups.Count > 0)
        {
            return Ready(
                "存档组已就绪。",
                reconciled.Groups,
                reconciled.Entry?.NextLaunchGroupId,
                _options.LiveSaveDirectory);
        }

        var installationDirectory = GetInstallationDirectory(installation);
        var live = _options.LiveSaveDirectory;

        try
        {
            if (HasDataFiles(installationDirectory))
                return MigrateLegacy(installation, installationDirectory, live);

            if (_options.Linker.Exists(live) && _options.Linker.TryGetTarget(live, out var linkTarget))
            {
                var target = Path.GetFullPath(linkTarget);
                if (!IsUnder(target, ProfilesRoot))
                {
                    return Failed(
                        $"现有存档链接指向 MK 之外的位置：{target}。为避免覆盖，未做任何修改。");
                }

                // A junction whose target no longer exists carries no data: treat
                // it as "no link" and let this instance build its first group.
                if (!Directory.Exists(target))
                {
                    _options.Linker.RemoveJunction(live, out var removeError);
                    if (_options.Linker.Exists(live))
                    {
                        return Failed(
                            $"现有存档链接指向已不存在的目录（{target}），且无法移除该链接：{removeError}。未做任何修改。");
                    }

                    return CreateEmptyDefault(
                        installation,
                        $"原存档链接指向已删除的目录（{target}），已清理并创建默认存档组。");
                }

                if (!PathEquals(target, installationDirectory) && !IsUnder(target, installationDirectory))
                {
                    // Read-only initialization never takes the link away from
                    // another installation; an explicit instance switch may.
                    if (!allowTakeover)
                    {
                        var owner = GetLiveOwner();
                        var described = owner is null
                            ? target
                            : $"另一个安装的存档组「{owner.Group.Name}」（{owner.GameDirectory}）";
                        return Failed($"现有存档链接属于{described}。未做任何修改。");
                    }

                    return CreateEmptyDefault(
                        installation,
                        $"该版本还没有存档组；已创建默认存档组并接管了存档链接（原链接：{target}）。");
                }

                if (HasDataFiles(target))
                    return MigrateLegacy(installation, target, live);

                return CreateEmptyDefault(installation, "存档目录为空，已创建默认存档组。");
            }

            if (_options.Linker.Exists(live))
            {
                var liveEntries = Directory.EnumerateFileSystemEntries(live).ToList();
                if (liveEntries.Count > 0)
                {
                    if (unmanagedAction is null)
                    {
                        return new SaveGroupInitializationResult(
                            SaveGroupInitializationState.NeedsUnmanagedChoice,
                            "检测到未托管的游戏存档；请选择导入这些存档、新建空组，或取消。取消不会修改任何文件。",
                            [],
                            null,
                            live,
                            liveEntries.Count);
                    }

                    return unmanagedAction == UnmanagedSaveAction.Import
                        ? ImportUnmanaged(installation, live)
                        : CreateEmptyOverUnmanaged(installation, live);
                }
            }

            return CreateEmptyDefault(installation, "首次使用，已创建空的默认存档组。");
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"初始化存档组失败：{exception.Message}");
            return Failed($"初始化存档组失败：{exception.Message}");
        }
    }

    /// <summary>
    /// Points the live LocalLow junction at the selected group during a launch.
    /// A failed switch never boots the game.
    /// </summary>
    public Task<SaveLaunchResult> PrepareLaunchAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => PrepareLaunchCore(installation), cancellationToken);
    }

    /// <summary>
    /// Applies a group immediately: switches the live junction (only when no
    /// game is running) and persists the selection. On failure the old link and
    /// the previous selection are kept.
    /// </summary>
    public Task<SaveGroupOperationResult> ApplyCurrentAsync(
        GameInstallation installation,
        Guid groupId,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ApplyCurrentCore(installation, groupId), cancellationToken);

    public SaveGroup? GetLiveGroup(GameInstallation installation)
    {
        lock (_writeGate)
        lock (_gate)
        {
            var groups = Reconcile(installation, persist: true).Groups;
            if (!_options.Linker.Exists(_options.LiveSaveDirectory)
                || !_options.Linker.TryGetTarget(_options.LiveSaveDirectory, out var target))
            {
                return null;
            }

            var full = Path.GetFullPath(target);
            return groups.FirstOrDefault(group => PathEquals(group.DirectoryPath, full));
        }
    }

    /// <summary>Resolves the live junction to the installation and group that own
    /// it, or null when the link is missing, dangling or outside the profiles
    /// root. The game writes one global LocalLow directory, so the owner is
    /// page-level state, not per-installation state.</summary>
    public SaveGroupOwner? GetLiveOwner()
    {
        lock (_writeGate)
        lock (_gate)
        {
            var live = _options.LiveSaveDirectory;
            if (!_options.Linker.Exists(live) || !_options.Linker.TryGetTarget(live, out var target))
                return null;

            var full = Path.GetFullPath(target);
            if (!Directory.Exists(full)) return null;

            foreach (var entry in _catalog.Load().Installations)
            {
                var group = _scanner.Scan(entry.InstallationKey)
                    .FirstOrDefault(candidate => PathEquals(candidate.DirectoryPath, full));
                if (group is not null)
                    return new SaveGroupOwner(entry.InstallationKey, group, entry.GameDirectory);
            }

            return null;
        }
    }

    public SaveLaunchPreview GetLaunchPreview(GameInstallation installation)
    {
        lock (_writeGate)
        lock (_gate)
        {
            var reconciled = Reconcile(installation, persist: true);
            var group = SelectGroup(reconciled.Groups, reconciled.Entry?.NextLaunchGroupId);
            if (group is null)
                return new SaveLaunchPreview(true, null, false, _options.LiveSaveDirectory);

            var live = _options.LiveSaveDirectory;
            var needsSwitch = true;
            if (_options.Linker.Exists(live) && _options.Linker.TryGetTarget(live, out var target))
                needsSwitch = !PathEquals(target, group.DirectoryPath);

            return new SaveLaunchPreview(false, group, needsSwitch, live);
        }
    }

    /// <summary>
    /// Points the live junction at the group this installation would use next.
    /// Called when the active instance changes and at startup: unlike a launch
    /// it also creates the first group (“默认”), and it never touches unmanaged
    /// saves — the caller resolves those first and calls again with the action.
    /// </summary>
    public Task<SaveGroupActivationResult> EnsureActiveGroupAsync(
        GameInstallation installation,
        UnmanagedSaveAction? unmanagedAction = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => EnsureActiveGroupCore(installation, unmanagedAction), cancellationToken);

    private SaveGroupActivationResult EnsureActiveGroupCore(
        GameInstallation installation,
        UnmanagedSaveAction? unmanagedAction)
    {
        lock (_writeGate)
        {
            var blocked = RunningBlock(
                installation,
                () => "游戏正在运行；请先关闭游戏再切换存档组。",
                () => "无法确认游戏是否正在运行（进程信息不可读）；为避免损坏存档，未切换存档组。");
            if (blocked is not null)
                return new SaveGroupActivationResult(SaveGroupActivationState.RunningLocked, blocked);

            var live = _options.LiveSaveDirectory;
            try
            {
                var (entry, groups) = Reconcile(installation, persist: true);

                if (groups.Count == 0)
                {
                    // First activation: migrate legacy data, import unmanaged
                    // saves (only after the caller asked) or create 「默认」.
                    // An explicit instance switch may also take the link over.
                    var init = InitializeBody(installation, unmanagedAction, allowTakeover: true);
                    if (init.State == SaveGroupInitializationState.NeedsUnmanagedChoice)
                    {
                        return new SaveGroupActivationResult(
                            SaveGroupActivationState.NeedsUnmanagedChoice,
                            init.Message,
                            null,
                            init.LiveDirectory,
                            init.UnmanagedFileCount);
                    }

                    if (!init.Success)
                    {
                        return new SaveGroupActivationResult(
                            SaveGroupActivationState.Failed,
                            init.Message,
                            null,
                            init.LiveDirectory);
                    }

                    var created = init.Groups.FirstOrDefault(candidate => candidate.Id == init.NextLaunchGroupId)
                                  ?? init.Groups.FirstOrDefault();
                    return new SaveGroupActivationResult(
                        SaveGroupActivationState.Switched,
                        init.Message,
                        created,
                        init.LiveDirectory);
                }

                var group = SelectGroup(groups, entry?.NextLaunchGroupId);
                if (group is null)
                {
                    return new SaveGroupActivationResult(
                        SaveGroupActivationState.Failed,
                        "没有可用的存档组。",
                        null,
                        live);
                }

                if (!Directory.Exists(group.DirectoryPath))
                {
                    return new SaveGroupActivationResult(
                        SaveGroupActivationState.Failed,
                        $"存档组目录不存在：{group.DirectoryPath}",
                        group,
                        live);
                }

                var switched = SwitchLiveLink(group, live);
                if (!switched.Success)
                {
                    return new SaveGroupActivationResult(
                        SaveGroupActivationState.Failed,
                        switched.Message,
                        group,
                        live);
                }

                if (entry is not null && entry.NextLaunchGroupId != group.Id)
                {
                    try
                    {
                        var catalog = _catalog.Load();
                        var catalogEntry = FindCatalogEntry(catalog, installation);
                        if (catalogEntry is null)
                            throw new InvalidOperationException("存档组索引缺失，请刷新后重试。");

                        catalogEntry.NextLaunchGroupId = group.Id;
                        _catalog.Save(catalog);
                    }
                    catch (Exception exception)
                    {
                        var restore = RestoreLiveLink(live, switched.PreviousTarget);
                        _options.Logger?.Warn($"同步存档组后保存索引失败：{exception.Message}");
                        return new SaveGroupActivationResult(
                            SaveGroupActivationState.Failed,
                            restore.Success
                                ? $"保存存档索引失败，链接已恢复：{exception.Message}"
                                : $"保存存档索引失败，原链接未恢复（{restore.Stage}失败：{restore.Error}）；请手动检查 {live}。",
                            group,
                            live);
                    }
                }

                return new SaveGroupActivationResult(
                    switched.Switched
                        ? SaveGroupActivationState.Switched
                        : SaveGroupActivationState.AlreadyActive,
                    switched.Message,
                    group,
                    live);
            }
            catch (Exception exception)
            {
                _options.Logger?.Warn($"由实例切换同步存档组失败：{exception.Message}");
                return new SaveGroupActivationResult(
                    SaveGroupActivationState.Failed,
                    $"切换存档组失败：{exception.Message}",
                    null,
                    live);
            }
        }
    }

    /// <summary>
    /// Sends one non-current group to the Windows recycle bin and removes its
    /// index record. Never falls back to a permanent delete.
    /// </summary>
    public Task<SaveGroupOperationResult> DeleteGroupAsync(
        GameInstallation installation,
        Guid groupId,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => DeleteGroupCore(installation, groupId), cancellationToken);

    /// <summary>
    /// Runs an external save operation (ZIP export/import) under the same lock
    /// as apply/delete/create. The lock is reentrant, so the operation may call
    /// back into this service (AdoptGroup) on the same thread.
    /// </summary>
    public T RunExclusive<T>(Func<T> operation)
    {
        lock (_writeGate)
        {
            return operation();
        }
    }

    private SaveGroupOperationResult ApplyCurrentCore(GameInstallation installation, Guid groupId) =>
        Mutate(installation, "应用存档组", () => ApplyCurrentBody(installation, groupId), Fail);

    private SaveGroupOperationResult ApplyCurrentBody(GameInstallation installation, Guid groupId)
    {
        if (!_options.Linker.IsSupported)
            return Fail("非 Windows 环境，无法切换存档链接。");

        var reconciled = Reconcile(installation, persist: true);
        var group = reconciled.Groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null)
            return Fail("要应用的存档组不存在，或不属于当前游戏目录。");

        if (!Directory.Exists(group.DirectoryPath))
            return Fail($"存档组目录不存在：{group.DirectoryPath}");

        var live = _options.LiveSaveDirectory;
        var switched = SwitchLiveLink(group, live);
        if (!switched.Success)
            return Fail(switched.Message);

        try
        {
            var catalog = _catalog.Load();
            var entry = FindCatalogEntry(catalog, installation);
            if (entry is null)
                throw new InvalidOperationException("存档组索引缺失，请刷新后重试。");

            entry.NextLaunchGroupId = groupId;
            _catalog.Save(catalog);
        }
        catch (Exception exception)
        {
            string linkState;
            if (!switched.Switched)
            {
                linkState = "存档链接未改动";
            }
            else
            {
                var restore = RestoreLiveLink(live, switched.PreviousTarget);
                var expected = switched.PreviousTarget ?? "（原本没有链接）";
                linkState = restore.Success
                    ? $"已恢复原链接：{expected}"
                    : $"原链接未恢复（{restore.Stage}失败：{restore.Error}），请手动检查 {live}，预期指向 {expected}";
            }

            _options.Logger?.Warn($"应用存档组时保存索引失败：{exception.Message}");
            return Fail($"应用存档组失败，存档索引未保存；{linkState}。原因：{exception.Message}");
        }

        _options.Logger?.Info($"当前存档组已应用：{group.Name}（{group.DirectoryPath}）");
        return new SaveGroupOperationResult(
            true,
            switched.Switched
                ? $"当前存档已切换到「{group.Name}」。"
                : $"「{group.Name}」已经是当前存档。",
            group);
    }
}
