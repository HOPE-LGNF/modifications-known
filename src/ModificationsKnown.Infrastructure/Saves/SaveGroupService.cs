using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Saves;

public sealed class SaveGroupOptions
{
    public string ProfilesRoot { get; init; } = AppPaths.SaveProfilesDirectory;

    public string CatalogPath { get; init; } = Path.Combine(AppPaths.RootDirectory, SaveGroupFormat.CatalogFileName);

    public string BackupsRoot { get; init; } = Path.Combine(AppPaths.RootDirectory, "save-backups");

    public string? StagingRoot { get; init; }

    public string LiveSaveDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Orsoniks", "CasualtiesUnknown");

    public ISaveLinker Linker { get; init; } = new WindowsJunctionLinker();

    public IAppLogger? Logger { get; init; }

    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Set by the app from the process monitor. Every operation that
    /// writes or copies saves must refuse while any game instance is running.</summary>
    public Func<GameInstallation, bool>? IsGameRunning { get; init; }
}

public enum SaveGroupInitializationState
{
    Ready,
    NeedsUnmanagedChoice,
    Cancelled,
    Failed
}

public enum UnmanagedSaveAction
{
    Import,
    CreateEmpty
}

public sealed record SaveGroupInitializationResult(
    SaveGroupInitializationState State,
    string Message,
    IReadOnlyList<SaveGroup> Groups,
    Guid? NextLaunchGroupId,
    string LiveDirectory,
    int UnmanagedFileCount = 0)
{
    public bool Success => State == SaveGroupInitializationState.Ready;
}

public sealed record SaveLaunchResult(
    bool Success,
    string Message,
    SaveGroup? Group = null,
    bool Switched = false);

public sealed record SaveLaunchPreview(
    bool NeedsInitialization,
    SaveGroup? Group,
    bool NeedsSwitch,
    string LiveDirectory);

/// <summary>
/// Owns save-group data: the catalog, per-group manifests, legacy profile
/// migration, first-run handling of unmanaged saves, and the launch-time
/// junction switch. All filesystem roots and the linker are injectable so tests
/// never touch the player's real LocalLow or AppData directories.
/// </summary>
public sealed class SaveGroupService
{
    private readonly SaveGroupOptions _options;
    private readonly SaveGroupCatalogStore _catalog;
    private readonly SaveGroupScanner _scanner;
    private readonly object _gate = new();

    public SaveGroupService(SaveGroupOptions? options = null)
    {
        _options = options ?? new SaveGroupOptions();
        _catalog = new SaveGroupCatalogStore(_options.CatalogPath);
        _scanner = new SaveGroupScanner(_options.ProfilesRoot, _options.Logger);
    }

    public string ProfilesRoot => _options.ProfilesRoot;

    public string LiveSaveDirectory => _options.LiveSaveDirectory;

    public string BackupsRoot => _options.BackupsRoot;

    public string GetInstallationKey(GameInstallation installation) =>
        SaveGroupFormat.GetInstallationKey(installation.GameDirectory);

    public string GetInstallationDirectory(GameInstallation installation) =>
        Path.Combine(ProfilesRoot, GetInstallationKey(installation));

    public string GetGroupDirectory(GameInstallation installation, Guid groupId) =>
        Path.Combine(GetInstallationDirectory(installation), groupId.ToString("N").ToLowerInvariant());

    /// <summary>
    /// Reconciles the index with the manifests on disk and returns the live
    /// groups (default first). Groups whose directory or manifest disappeared
    /// are dropped; a corrupt manifest is skipped but never deleted.
    /// </summary>
    public IReadOnlyList<SaveGroup> GetGroups(GameInstallation installation)
    {
        lock (_gate)
        {
            return Reconcile(installation, persist: true).Groups;
        }
    }

    public Guid? GetNextLaunchGroupId(GameInstallation installation)
    {
        lock (_gate)
        {
            return Reconcile(installation, persist: true).Entry?.NextLaunchGroupId;
        }
    }

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
        UnmanagedSaveAction? unmanagedAction)
    {
        if (!_options.Linker.IsSupported)
        {
            return Ready(
                "非 Windows 环境，跳过存档隔离。",
                [],
                null,
                "非 Windows 环境");
        }

        if (IsGameRunning(installation))
            return Failed("游戏正在运行；请先关闭游戏再操作存档组。");

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

                if (!PathEquals(target, installationDirectory))
                {
                    return Failed(
                        $"现有存档链接指向另一个安装的存档目录：{target}。未做任何修改。");
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
    /// Points the live LocalLow junction at the selected group. This is the only
    /// place a launch changes the link; selecting a group only changes the
    /// preference. A failed switch never boots the game.
    /// </summary>
    public Task<SaveLaunchResult> PrepareLaunchAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => PrepareLaunchCore(installation), cancellationToken);
    }

    public SaveLaunchPreview GetLaunchPreview(GameInstallation installation)
    {
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

    private SaveLaunchResult PrepareLaunchCore(GameInstallation installation)
    {
        if (!_options.Linker.IsSupported)
            return new SaveLaunchResult(true, "非 Windows 环境，跳过存档隔离。");

        if (IsGameRunning(installation))
            return new SaveLaunchResult(false, "游戏正在运行；请先关闭游戏再切换存档组。");

        var reconciled = Reconcile(installation, persist: true);
        var group = SelectGroup(reconciled.Groups, reconciled.Entry?.NextLaunchGroupId);
        if (group is null)
            return new SaveLaunchResult(false, "还没有可用的存档组；请先在存档管理中创建或迁移。");

        if (!Directory.Exists(group.DirectoryPath))
            return new SaveLaunchResult(false, $"存档组目录不存在：{group.DirectoryPath}", group);

        var live = _options.LiveSaveDirectory;
        try
        {
            if (_options.Linker.Exists(live))
            {
                if (_options.Linker.TryGetTarget(live, out var currentTarget))
                {
                    var current = Path.GetFullPath(currentTarget);
                    if (!IsUnder(current, ProfilesRoot))
                    {
                        return new SaveLaunchResult(
                            false,
                            $"现有存档链接指向 MK 之外的位置：{current}。未切换，游戏不会启动。",
                            group);
                    }

                    if (PathEquals(current, group.DirectoryPath))
                        return new SaveLaunchResult(true, $"存档已指向「{group.Name}」。", group);

                    if (!_options.Linker.RemoveJunction(live, out var removeError))
                    {
                        return new SaveLaunchResult(
                            false,
                            $"切换存档组失败：无法移除现有链接（{removeError}）。游戏不会启动。",
                            group);
                    }

                    if (!_options.Linker.CreateJunction(live, group.DirectoryPath, out var createError))
                    {
                        var restored = _options.Linker.CreateJunction(live, current, out var restoreError);
                        var recovery = restored
                            ? $"已恢复原链接：{current}"
                            : $"未能恢复原链接（{restoreError}），请手动检查 {live}。";
                        return new SaveLaunchResult(
                            false,
                            $"切换存档组失败：{createError}。{recovery} 游戏不会启动。",
                            group);
                    }

                    return new SaveLaunchResult(true, $"存档已切换到「{group.Name}」。", group, Switched: true);
                }

                if (Directory.EnumerateFileSystemEntries(live).Any())
                {
                    return new SaveLaunchResult(
                        false,
                        $"检测到未托管的存档目录：{live}。请先在存档管理中导入或处理，游戏不会启动。",
                        group);
                }

                Directory.Delete(live, recursive: false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(live)!);
            if (!_options.Linker.CreateJunction(live, group.DirectoryPath, out var firstLinkError))
            {
                return new SaveLaunchResult(
                    false,
                    $"创建存档链接失败：{firstLinkError}。游戏不会启动。",
                    group);
            }

            return new SaveLaunchResult(true, $"存档已指向「{group.Name}」。", group, Switched: true);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"切换存档组失败：{exception.Message}");
            return new SaveLaunchResult(false, $"切换存档组失败：{exception.Message}。游戏不会启动。", group);
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

            return Ready(
                $"已把旧存档迁移到默认存档组；原目录保留为回退副本（{sourceDirectory}）。",
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
                RestoreBackup(backup, live);
                RollbackGroup(installation, group);
                return Failed($"导入存档后创建隔离链接失败，已恢复原目录：{linkError}");
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
            if (backup is not null) RestoreBackup(backup, live);
            if (group is not null) RollbackGroup(installation, group);
            _options.Logger?.Warn($"导入未托管存档失败：{exception.Message}");
            return Failed($"导入未托管存档失败，原存档保持不变：{exception.Message}");
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
                RestoreBackup(backup, live);
                RollbackGroup(installation, group);
                return Failed($"新建空组后创建隔离链接失败，已恢复原目录：{linkError}");
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
            if (backup is not null) RestoreBackup(backup, live);
            if (group is not null) RollbackGroup(installation, group);
            _options.Logger?.Warn($"新建空组失败：{exception.Message}");
            return Failed($"新建空组失败，原存档保持不变：{exception.Message}");
        }
    }

    private SaveGroupInitializationResult CreateEmptyDefault(GameInstallation installation, string message)
    {
        var staging = NewStagingDirectory(out var groupId);
        try
        {
            var group = CommitGroup(installation, staging, groupId, "默认", isDefault: true, _options.UtcNow());
            return Ready(message, GroupListAfterCommit(installation), group.Id, _options.LiveSaveDirectory);
        }
        catch (Exception exception)
        {
            DeleteDirectoryQuietly(staging);
            _options.Logger?.Warn($"创建默认存档组失败：{exception.Message}");
            return Failed($"创建默认存档组失败：{exception.Message}");
        }
    }

    private SaveGroup CommitGroup(
        GameInstallation installation,
        string stagingDirectory,
        Guid groupId,
        string name,
        bool isDefault,
        DateTimeOffset createdAt)
    {
        var installationKey = GetInstallationKey(installation);
        var manifest = new SaveGroupManifest
        {
            SchemaVersion = SaveGroupFormat.ManifestSchemaVersion,
            GroupId = groupId,
            Name = name,
            CreatedAt = createdAt,
            InstallationKey = installationKey,
            IsDefault = isDefault
        };
        SaveGroupManifestStore.Write(stagingDirectory, manifest);

        var installationDirectory = GetInstallationDirectory(installation);
        Directory.CreateDirectory(installationDirectory);
        var finalDirectory = GetGroupDirectory(installation, groupId);
        if (Directory.Exists(finalDirectory))
            throw new IOException($"存档组目录已存在：{finalDirectory}");

        Directory.Move(stagingDirectory, finalDirectory);

        try
        {
            var catalog = _catalog.Load();
            var entry = SaveGroupCatalogStore.GetOrCreate(
                catalog, installationKey, installation.GameDirectory);
            entry.Groups.RemoveAll(record => record.Id == groupId);
            entry.Groups.Add(new SaveGroupRecord
            {
                Id = groupId,
                Name = name,
                CreatedAt = createdAt,
                IsDefault = isDefault
            });
            entry.NextLaunchGroupId ??= groupId;
            _catalog.Save(catalog);
        }
        catch
        {
            DeleteDirectoryQuietly(finalDirectory);
            throw;
        }

        return new SaveGroup(groupId, name, createdAt, isDefault, finalDirectory);
    }

    private void RollbackGroup(GameInstallation installation, SaveGroup group)
    {
        DeleteDirectoryQuietly(group.DirectoryPath);

        try
        {
            var catalog = _catalog.Load();
            var entry = catalog.Installations.FirstOrDefault(candidate =>
                string.Equals(candidate.InstallationKey, GetInstallationKey(installation),
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;

            entry.Groups.RemoveAll(record => record.Id == group.Id);
            if (entry.NextLaunchGroupId == group.Id)
                entry.NextLaunchGroupId = entry.Groups.OrderBy(record => record.CreatedAt).FirstOrDefault()?.Id;
            _catalog.Save(catalog);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"回滚存档组索引失败：{exception.Message}");
        }
    }

    private IReadOnlyList<SaveGroup> GroupListAfterCommit(GameInstallation installation)
    {
        var reconciled = Reconcile(installation, persist: true);
        return reconciled.Groups;
    }

    private (SaveGroupInstallationEntry? Entry, IReadOnlyList<SaveGroup> Groups) Reconcile(
        GameInstallation installation,
        bool persist)
    {
        var key = GetInstallationKey(installation);
        var scanned = _scanner.Scan(key);

        var catalog = _catalog.Load();
        var entry = catalog.Installations.FirstOrDefault(candidate =>
            string.Equals(candidate.InstallationKey, key, StringComparison.OrdinalIgnoreCase));

        var changed = false;
        if (entry is null)
        {
            if (scanned.Count > 0)
            {
                entry = SaveGroupCatalogStore.GetOrCreate(catalog, key, installation.GameDirectory);
                changed = true;
            }
        }
        else if (!string.Equals(entry.GameDirectory, Path.GetFullPath(installation.GameDirectory),
                     StringComparison.OrdinalIgnoreCase))
        {
            entry.GameDirectory = Path.GetFullPath(installation.GameDirectory);
            changed = true;
        }

        if (entry is not null)
        {
            var liveIds = scanned.Select(group => group.Id).ToHashSet();
            if (entry.Groups.RemoveAll(record => !liveIds.Contains(record.Id)) > 0)
                changed = true;

            foreach (var group in scanned)
            {
                var record = entry.Groups.FirstOrDefault(candidate => candidate.Id == group.Id);
                if (record is null)
                {
                    entry.Groups.Add(new SaveGroupRecord
                    {
                        Id = group.Id,
                        Name = group.Name,
                        CreatedAt = group.CreatedAt,
                        IsDefault = group.IsDefault
                    });
                    changed = true;
                    continue;
                }

                if (!string.Equals(record.Name, group.Name, StringComparison.Ordinal)
                    || record.IsDefault != group.IsDefault
                    || record.CreatedAt != group.CreatedAt)
                {
                    record.Name = group.Name;
                    record.IsDefault = group.IsDefault;
                    record.CreatedAt = group.CreatedAt;
                    changed = true;
                }
            }

            var ordered = entry.Groups
                .OrderByDescending(record => record.IsDefault)
                .ThenBy(record => record.CreatedAt)
                .ToList();
            if (!ordered.SequenceEqual(entry.Groups))
            {
                entry.Groups = ordered;
                changed = true;
            }

            if (entry.Groups.Count > 0
                && (entry.NextLaunchGroupId is null
                    || entry.Groups.All(record => record.Id != entry.NextLaunchGroupId)))
            {
                entry.NextLaunchGroupId = ordered.FirstOrDefault(record => record.IsDefault)?.Id
                                          ?? ordered[0].Id;
                changed = true;
            }
            else if (entry.Groups.Count == 0 && entry.NextLaunchGroupId is not null)
            {
                entry.NextLaunchGroupId = null;
                changed = true;
            }
        }

        if (changed && persist)
        {
            try
            {
                _catalog.Save(catalog);
            }
            catch (Exception exception)
            {
                _options.Logger?.Warn($"保存存档组索引失败：{exception.Message}");
            }
        }

        var groups = scanned
            .OrderByDescending(group => group.IsDefault)
            .ThenBy(group => group.CreatedAt)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (entry, groups);
    }

    private static SaveGroup? SelectGroup(IReadOnlyList<SaveGroup> groups, Guid? selectedId)
    {
        if (groups.Count == 0) return null;
        if (selectedId is not null)
        {
            var selected = groups.FirstOrDefault(group => group.Id == selectedId.Value);
            if (selected is not null) return selected;
        }

        return groups.FirstOrDefault(group => group.IsDefault) ?? groups[0];
    }

    private string NewStagingDirectory(out Guid groupId)
    {
        groupId = Guid.NewGuid();
        var root = _options.StagingRoot
                   ?? Path.Combine(Path.GetDirectoryName(ProfilesRoot) ?? ProfilesRoot, "save-groups-staging");
        var staging = Path.Combine(root, groupId.ToString("N").ToLowerInvariant());
        Directory.CreateDirectory(staging);
        return staging;
    }

    private string MoveLiveToBackup(string live)
    {
        Directory.CreateDirectory(_options.BackupsRoot);
        var key = Path.GetFileName(Path.TrimEndingDirectorySeparator(live));
        var stamp = _options.UtcNow().ToString("yyyyMMddHHmmss");
        var backup = Path.Combine(_options.BackupsRoot, $"{key}-{stamp}");
        var suffix = 2;
        while (Directory.Exists(backup))
            backup = Path.Combine(_options.BackupsRoot, $"{key}-{stamp}-{suffix++}");

        Directory.Move(live, backup);
        _options.Logger?.Info($"原存档目录已备份到 {backup}");
        return backup;
    }

    private void RestoreBackup(string backup, string live)
    {
        try
        {
            if (Directory.Exists(live) && !Directory.EnumerateFileSystemEntries(live).Any())
                Directory.Delete(live, recursive: false);
            if (!Directory.Exists(live) && Directory.Exists(backup))
                Directory.Move(backup, live);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"恢复原存档目录失败：{exception.Message}");
        }
    }

    private IReadOnlyList<string> CollectDataFiles(string sourceDirectory)
    {
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var firstSegment = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                2)[0];

            if (!string.Equals(firstSegment, relative, StringComparison.Ordinal)
                && IsGroupDirectory(Path.Combine(sourceDirectory, firstSegment)))
            {
                continue;
            }

            if (SaveGroupFormat.IsInternalFile(Path.GetFileName(file))) continue;
            files.Add(relative);
        }

        return files;
    }

    private static void CopyFiles(string sourceDirectory, string destinationDirectory, IReadOnlyList<string> files)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var relative in files)
        {
            var source = Path.Combine(sourceDirectory, relative);
            var destination = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static void VerifyCopy(string sourceDirectory, string destinationDirectory, IReadOnlyList<string> files)
    {
        foreach (var relative in files)
        {
            var source = Path.Combine(sourceDirectory, relative);
            var destination = Path.Combine(destinationDirectory, relative);
            if (!SaveFileHasher.HashesMatch(source, destination))
                throw new IOException($"存档文件校验失败：{relative}");
        }
    }

    private bool HasDataFiles(string directory)
    {
        if (!Directory.Exists(directory)) return false;

        try
        {
            return CollectDataFiles(directory).Count > 0;
        }
        catch
        {
            // Unreadable data must block migration rather than look empty.
            return true;
        }
    }

    private static bool IsGroupDirectory(string directory) =>
        File.Exists(SaveGroupManifestStore.GetManifestPath(directory));

    private bool IsGameRunning(GameInstallation installation) =>
        _options.IsGameRunning?.Invoke(installation) == true;

    private SaveGroupInitializationResult Ready(
        string message,
        IReadOnlyList<SaveGroup> groups,
        Guid? nextLaunchGroupId,
        string live) =>
        new(SaveGroupInitializationState.Ready, message, groups, nextLaunchGroupId, live);

    private SaveGroupInitializationResult Failed(string message) =>
        new(SaveGroupInitializationState.Failed, message, [], null, _options.LiveSaveDirectory);

    private static void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best effort; the caller has already reported the primary failure.
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
