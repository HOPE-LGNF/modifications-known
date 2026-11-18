using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Saves;

public sealed partial class SaveGroupService
{
    public Task<SaveGroupOperationResult> CreateEmptyAsync(
        GameInstallation installation,
        string name,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => CreateEmptyCore(installation, name), cancellationToken);

    /// <summary>
    /// Copies a complete group directory (minus the group manifest and legacy
    /// marker) into a new group with a fresh id, verifying every file hash. The
    /// source group is never modified.
    /// </summary>
    public Task<SaveGroupOperationResult> CloneAsync(
        GameInstallation installation,
        Guid sourceGroupId,
        string name,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => CloneCore(installation, sourceGroupId, name), cancellationToken);

    /// <summary>Renames a group in its manifest and in the index. The directory
    /// name (the group id) never changes.</summary>
    public Task<SaveGroupOperationResult> RenameAsync(
        GameInstallation installation,
        Guid groupId,
        string name,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RenameCore(installation, groupId, name), cancellationToken);

    /// <summary>
    /// Stores which group the next MK launch uses. This changes only the
    /// preference; the live junction keeps its current target until launch.
    /// </summary>
    public Task<SaveGroupOperationResult> SelectNextLaunchAsync(
        GameInstallation installation,
        Guid groupId,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => SelectNextLaunchCore(installation, groupId), cancellationToken);

    /// <summary>
    /// Registers a fully extracted staging directory as a new group (used by ZIP
    /// import/restore). The staging directory must live under
    /// <see cref="StagingRoot"/>; a new group id and manifest are always written,
    /// so an imported group never reuses the archive's identity or overwrites an
    /// existing group. The caller cleans staging when this fails.
    /// </summary>
    public Task<SaveGroupOperationResult> AdoptGroupAsync(
        GameInstallation installation,
        string stagingDirectory,
        string name,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => AdoptGroup(installation, stagingDirectory, name), cancellationToken);

    /// <summary>Synchronous core of <see cref="AdoptGroupAsync"/> for callers
    /// that already run on a worker thread (ZIP import).</summary>
    public SaveGroupOperationResult AdoptGroup(
        GameInstallation installation,
        string stagingDirectory,
        string name) =>
        AdoptGroupCore(installation, stagingDirectory, name);

    private SaveGroupOperationResult AdoptGroupCore(
        GameInstallation installation,
        string stagingDirectory,
        string name) =>
        Mutate(installation, "导入存档组", () => AdoptGroupBody(installation, stagingDirectory, name), Fail);

    private SaveGroupOperationResult AdoptGroupBody(
        GameInstallation installation,
        string stagingDirectory,
        string name)
    {
        var validated = ValidateNewName(installation, name, null, out var normalized);
        if (validated is not null) return validated;

        try
        {
            var staging = Path.GetFullPath(stagingDirectory);
            var stagingRoot = Path.GetFullPath(StagingRoot);
            if (!IsUnder(staging, stagingRoot) || PathEquals(staging, stagingRoot))
                return Fail("导入暂存目录不在允许的位置；未登记存档组。");

            var installationDirectory = Path.GetFullPath(GetInstallationDirectory(installation));
            if (IsUnder(staging, installationDirectory) || IsUnder(installationDirectory, staging))
                return Fail("导入暂存目录与安装存档目录重叠；未登记存档组。");

            if (!Directory.Exists(staging))
                return Fail($"导入暂存目录不存在：{staging}");

            if (File.Exists(SaveGroupManifestStore.GetManifestPath(staging)))
                return Fail("导入暂存目录已带有存档组清单；拒绝重复登记。");

            bool isFirst;
            lock (_gate)
            {
                isFirst = Reconcile(installation, persist: true).Groups.Count == 0;
            }

            var group = CommitGroup(
                installation,
                staging,
                Guid.NewGuid(),
                normalized,
                isFirst,
                _options.UtcNow());
            _options.Logger?.Info($"已导入存档组「{normalized}」：{group.DirectoryPath}");

            if (isFirst)
            {
                if (LiveLinkBelongsToAnotherInstallation(installation))
                {
                    _options.Logger?.Info(
                        $"导入的首个存档组「{normalized}」未自动应用：当前链接属于另一个安装。");
                    return new SaveGroupOperationResult(
                        true,
                        $"已导入存档组「{normalized}」；当前存档链接属于另一个安装，未自动切换。" +
                        "如需使用该组，请在存档页点击「选择存档组」。",
                        group);
                }

                var applied = SwitchLiveLink(group, _options.LiveSaveDirectory);
                if (!applied.Success)
                {
                    // The imported copy is kept: deleting it would destroy data
                    // the user just asked to restore.
                    _options.Logger?.Warn($"导入首个存档组后切换链接失败：{applied.Message}");
                    return new SaveGroupOperationResult(
                        false,
                        $"已导入存档组「{normalized}」，但未能应用为当前存档：{applied.Message}",
                        group);
                }

                return new SaveGroupOperationResult(
                    true,
                    $"已导入存档组「{normalized}」并应用为当前存档。",
                    group);
            }

            return new SaveGroupOperationResult(true, $"已导入存档组「{normalized}」。", group);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"登记导入存档组失败：{exception.Message}");
            return Fail($"登记导入存档组失败：{exception.Message}");
        }
    }

    /// <summary>Returns a valid, unused name for this installation (used as the
    /// editable default when creating or cloning).</summary>
    public string SuggestGroupName(GameInstallation installation, string desiredName)
    {
        lock (_gate)
        {
            var groups = Reconcile(installation, persist: false).Groups;
            var baseName = SaveGroupFormat.SanitizeName(desiredName);
            if (baseName.Length == 0) baseName = "新存档组";

            var candidate = baseName;
            var index = 2;
            while (NameExists(groups, candidate, null))
            {
                var suffix = $" {index++}";
                var room = SaveGroupFormat.MaxNameLength - suffix.Length;
                var stem = baseName.Length > room ? baseName[..room].TrimEnd() : baseName;
                candidate = stem + suffix;
            }

            return candidate;
        }
    }

    private SaveGroupOperationResult CreateEmptyCore(GameInstallation installation, string name) =>
        Mutate(installation, "创建存档组", () => CreateEmptyBody(installation, name), Fail);

    private SaveGroupOperationResult CreateEmptyBody(GameInstallation installation, string name)
    {
        var validated = ValidateNewName(installation, name, null, out var normalized);
        if (validated is not null) return validated;

        var isFirst = Reconcile(installation, persist: true).Groups.Count == 0;
        var staging = NewStagingDirectory(out var groupId);
        var live = _options.LiveSaveDirectory;
        SaveGroup? group = null;
        try
        {
            group = CommitGroup(installation, staging, groupId, normalized, isDefault: false, _options.UtcNow());
            _options.Logger?.Info($"已创建空存档组「{normalized}」：{group.DirectoryPath}");

            if (isFirst)
            {
                if (LiveLinkBelongsToAnotherInstallation(installation))
                {
                    _options.Logger?.Info(
                        $"首个存档组「{normalized}」未自动应用：当前链接属于另一个安装。");
                    return new SaveGroupOperationResult(
                        true,
                        $"已创建空存档组「{normalized}」；当前存档链接属于另一个安装，未自动切换。" +
                        "如需使用该组，请在存档页点击「选择存档组」。",
                        group);
                }

                var applied = SwitchLiveLink(group, live);
                if (!applied.Success)
                {
                    // One logical operation: discard the empty group only when
                    // the previous live state is confirmed and nothing points at
                    // it; otherwise keep it for inspection.
                    if (applied.PreviousStateIntact && CanDiscardGroup(live, group))
                    {
                        RollbackGroup(installation, group);
                        _options.Logger?.Warn($"首个存档组创建后切换链接失败：{applied.Message}");
                        return Fail($"创建存档组失败，已回滚：{applied.Message}");
                    }

                    _options.Logger?.Warn($"首个存档组创建后切换链接失败（新组保留）：{applied.Message}");
                    return new SaveGroupOperationResult(
                        false,
                        $"已创建空存档组「{normalized}」，但未能应用为当前存档：{applied.Message}" +
                        $"该组保留在 {group.DirectoryPath}；请手动检查当前存档链接后再决定是否删除。",
                        group);
                }

                return new SaveGroupOperationResult(
                    true,
                    $"已创建空存档组「{normalized}」并应用为当前存档。",
                    group);
            }

            return new SaveGroupOperationResult(
                true,
                $"已创建空存档组「{normalized}」；首次用该组启动游戏后会生成存档文件。",
                group);
        }
        catch (Exception exception)
        {
            DeleteDirectoryQuietly(staging);
            _options.Logger?.Warn($"创建存档组失败：{exception.Message}");
            if (group is not null && !CanDiscardGroup(live, group))
            {
                // The live link might still point at this empty group, so it may
                // not be deleted: report the kept group instead.
                return Fail($"创建存档组失败：{exception.Message}；新组「{group.Name}」保留在 {group.DirectoryPath}，" +
                            "请手动检查当前存档链接。");
            }

            if (group is not null) RollbackGroup(installation, group);
            return Fail($"创建存档组失败：{exception.Message}");
        }
    }

    private SaveGroupOperationResult CloneCore(
        GameInstallation installation,
        Guid sourceGroupId,
        string name) =>
        Mutate(installation, "克隆存档组", () => CloneBody(installation, sourceGroupId, name), Fail);

    private SaveGroupOperationResult CloneBody(
        GameInstallation installation,
        Guid sourceGroupId,
        string name)
    {
        var validated = ValidateNewName(installation, name, null, out var normalized);
        if (validated is not null) return validated;

        SaveGroup? source;
        lock (_gate)
        {
            source = Reconcile(installation, persist: true).Groups
                .FirstOrDefault(group => group.Id == sourceGroupId);
        }

        if (source is null)
            return Fail("要克隆的存档组不存在，或不属于当前游戏目录。");
        if (!Directory.Exists(source.DirectoryPath))
            return Fail($"要克隆的存档组目录不存在：{source.DirectoryPath}");

        var staging = NewStagingDirectory(out var groupId);
        SaveGroup? clone = null;
        try
        {
            var files = CollectDataFiles(source.DirectoryPath);
            CopyDirectoryTree(source.DirectoryPath, staging);
            CopyFiles(source.DirectoryPath, staging, files);
            VerifyCopy(source.DirectoryPath, staging, files);

            clone = CommitGroup(installation, staging, groupId, normalized, isDefault: false, _options.UtcNow());
            _options.Logger?.Info($"已克隆存档组「{source.Name}」为「{normalized}」：{clone.DirectoryPath}");
            return new SaveGroupOperationResult(
                true,
                $"已克隆「{source.Name}」为「{normalized}」，共 {files.Count} 个文件并逐文件校验。",
                clone);
        }
        catch (Exception exception)
        {
            DeleteDirectoryQuietly(staging);
            if (clone is not null) RollbackGroup(installation, clone);
            _options.Logger?.Warn($"克隆存档组失败：{exception.Message}");
            return Fail($"克隆存档组失败，源组未改动：{exception.Message}");
        }
    }

    private SaveGroupOperationResult RenameCore(
        GameInstallation installation,
        Guid groupId,
        string name) =>
        Mutate(installation, "重命名存档组", () => RenameBody(installation, groupId, name), Fail);

    private SaveGroupOperationResult RenameBody(
        GameInstallation installation,
        Guid groupId,
        string name)
    {
        var validated = ValidateNewName(installation, name, groupId, out var normalized);
        if (validated is not null) return validated;

        lock (_gate)
        {
            var group = Reconcile(installation, persist: true).Groups
                .FirstOrDefault(candidate => candidate.Id == groupId);
            if (group is null)
                return Fail("要重命名的存档组不存在，或不属于当前游戏目录。");

            if (!SaveGroupManifestStore.TryRead(group.DirectoryPath, out var manifest))
                return Fail($"存档组清单缺失或损坏：{group.DirectoryPath}");

            var previousName = manifest.Name;
            try
            {
                manifest.Name = normalized;
                SaveGroupManifestStore.Write(group.DirectoryPath, manifest);

                var catalog = _catalog.Load();
                var entry = FindCatalogEntry(catalog, installation);
                var record = entry?.Groups.FirstOrDefault(candidate => candidate.Id == groupId);
                if (record is null)
                    throw new InvalidOperationException("存档组索引中找不到该组，请刷新后重试。");

                record.Name = normalized;
                _catalog.Save(catalog);
            }
            catch (Exception exception)
            {
                TryRestoreManifestName(group.DirectoryPath, manifest, previousName);
                _options.Logger?.Warn($"重命名存档组失败：{exception.Message}");
                return Fail($"重命名存档组失败：{exception.Message}");
            }

            _options.Logger?.Info($"存档组已重命名：{group.Name} -> {normalized}");
            return new SaveGroupOperationResult(
                true,
                $"已将存档组「{previousName}」重命名为「{normalized}」。",
                group with { Name = normalized });
        }
    }

    private SaveGroupOperationResult SelectNextLaunchCore(GameInstallation installation, Guid groupId) =>
        Mutate(installation, "选择下次启动的存档组", () => SelectNextLaunchBody(installation, groupId), Fail);

    private SaveGroupOperationResult SelectNextLaunchBody(GameInstallation installation, Guid groupId)
    {
        lock (_gate)
        {
            var group = Reconcile(installation, persist: true).Groups
                .FirstOrDefault(candidate => candidate.Id == groupId);
            if (group is null)
                return Fail("要选择的存档组不存在，或不属于当前游戏目录。");

            var catalog = _catalog.Load();
            var entry = FindCatalogEntry(catalog, installation);
            if (entry is null)
                return Fail("存档组索引缺失，请刷新后重试。");

            if (entry.NextLaunchGroupId == groupId)
            {
                return new SaveGroupOperationResult(
                    true,
                    $"「{group.Name}」已经是下次 MK 启动使用的存档组。",
                    group);
            }

            entry.NextLaunchGroupId = groupId;
            try
            {
                _catalog.Save(catalog);
            }
            catch (Exception exception)
            {
                _options.Logger?.Warn($"保存下次启动存档组失败：{exception.Message}");
                return Fail($"保存下次启动存档组失败：{exception.Message}");
            }

            _options.Logger?.Info($"下次 MK 启动将使用存档组「{group.Name}」（链接不变，启动前才切换）");
            return new SaveGroupOperationResult(
                true,
                $"下次由 MK 启动时使用「{group.Name}」；存档链接会在启动前切换。",
                group);
        }
    }

    /// <summary>Shared validation for create/clone/rename: usable name and no
    /// other group in this installation using it (case-insensitive).</summary>
    private SaveGroupOperationResult? ValidateNewName(
        GameInstallation installation,
        string name,
        Guid? exceptGroupId,
        out string normalized)
    {
        normalized = SaveGroupFormat.NormalizeName(name);
        var invalid = SaveGroupFormat.ValidateName(name);
        if (invalid is not null) return Fail(invalid);

        lock (_gate)
        {
            var groups = Reconcile(installation, persist: true).Groups;
            if (NameExists(groups, normalized, exceptGroupId))
                return Fail($"已存在同名存档组「{normalized}」；请换一个名称。");
        }

        return null;
    }

    private static bool NameExists(IReadOnlyList<SaveGroup> groups, string name, Guid? exceptGroupId)
    {
        var trimmed = SaveGroupFormat.NormalizeName(name);
        return groups.Any(group =>
            group.Id != exceptGroupId
            && string.Equals(group.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private void TryRestoreManifestName(string groupDirectory, SaveGroupManifest manifest, string name)
    {
        try
        {
            manifest.Name = name;
            SaveGroupManifestStore.Write(groupDirectory, manifest);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"恢复存档组清单名称失败：{exception.Message}");
        }
    }

    /// <summary>Creates the source directory tree in the destination so clones
    /// keep empty folders, which the file-only copy would drop.</summary>
    private static void CopyDirectoryTree(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }
    }

    private static SaveGroupOperationResult Fail(string message) => new(false, message);
}
