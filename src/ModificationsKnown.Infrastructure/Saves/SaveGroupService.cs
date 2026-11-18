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

    public string LiveSaveDirectory { get; init; } = ResolveLiveSaveDirectory();

    private static string ResolveLiveSaveDirectory() =>
        AppPaths.AcceptanceRoot is { } acceptanceRoot
            ? Path.Combine(acceptanceRoot, "LocalLow", "Orsoniks", "CasualtiesUnknown")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Orsoniks", "CasualtiesUnknown");

    public ISaveLinker Linker { get; init; } = new WindowsJunctionLinker();

    public IRecycleBin RecycleBin { get; init; } = new WindowsRecycleBin();

    public IAppLogger? Logger { get; init; }

    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Set by the app from the process monitor. Every operation that
    /// writes or copies saves must refuse while any game instance is running.</summary>
    public Func<GameInstallation, bool>? IsGameRunning { get; init; }

    /// <summary>
    /// Fresh, three-state probe used by every mutating operation. It is called
    /// while the save-group write lock is held, so it must scan the process list
    /// again instead of returning a cached monitor snapshot. <c>Unknown</c> is
    /// treated as running (fail closed); when unset, <see cref="IsGameRunning"/>
    /// is used and cannot report <c>Unknown</c>.
    /// </summary>
    public Func<GameInstallation, GameRunningState>? ProbeGameRunningState { get; init; }
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

public sealed record SaveGroupOperationResult(
    bool Success,
    string Message,
    SaveGroup? Group = null);

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

    // One lock for every operation that can change the live junction or move
    // group files. The running-state probe runs while it is held, so a launch
    // can never slip between "game is not running" and the switch itself.
    private readonly object _writeGate = new();

    public SaveGroupService(SaveGroupOptions? options = null)
    {
        _options = options ?? new SaveGroupOptions();
        _catalog = new SaveGroupCatalogStore(_options.CatalogPath);
        _scanner = new SaveGroupScanner(_options.ProfilesRoot, _options.Logger);
    }

    public string ProfilesRoot => _options.ProfilesRoot;

    public string LiveSaveDirectory => _options.LiveSaveDirectory;

    public string BackupsRoot => _options.BackupsRoot;

    public string StagingRoot => _options.StagingRoot
                                 ?? Path.Combine(Path.GetDirectoryName(ProfilesRoot) ?? ProfilesRoot, "save-groups-staging");

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
        // Reads may repair the index, so they take the same lock as writes:
        // a read's Load→modify→Save must not overwrite a concurrent write.
        lock (_writeGate)
        lock (_gate)
        {
            return Reconcile(installation, persist: true).Groups;
        }
    }

    public Guid? GetNextLaunchGroupId(GameInstallation installation)
    {
        lock (_writeGate)
        lock (_gate)
        {
            return Reconcile(installation, persist: true).Entry?.NextLaunchGroupId;
        }
    }

    /// <summary>
    /// Creates a new, empty group. The first group of an installation becomes
    /// the next-launch group automatically; later groups leave the preference
    /// alone (the caller asks the user whether to switch).
    /// </summary>
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
                        "如需使用该组，请在存档页选择后点「应用当前存档组」。",
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
            var baseName = SanitizeName(desiredName);
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
                        "如需使用该组，请在存档页选择后点「应用当前存档组」。",
                        group);
                }

                var applied = SwitchLiveLink(group, _options.LiveSaveDirectory);
                if (!applied.Success)
                {
                    RollbackGroup(installation, group);
                    _options.Logger?.Warn($"首个存档组创建后切换链接失败：{applied.Message}");
                    return Fail($"创建存档组失败，已回滚：{applied.Message}");
                }

                return new SaveGroupOperationResult(
                    true,
                    $"已创建空存档组「{normalized}」并应用为当前存档；从 Steam 启动也会使用该组。",
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
            if (group is not null) RollbackGroup(installation, group);
            _options.Logger?.Warn($"创建存档组失败：{exception.Message}");
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
                var entry = catalog.Installations.FirstOrDefault(candidate =>
                    string.Equals(candidate.InstallationKey, GetInstallationKey(installation),
                        StringComparison.OrdinalIgnoreCase));
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
            var entry = catalog.Installations.FirstOrDefault(candidate =>
                string.Equals(candidate.InstallationKey, GetInstallationKey(installation),
                    StringComparison.OrdinalIgnoreCase));
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

    private static string SanitizeName(string? name)
    {
        var trimmed = SaveGroupFormat.NormalizeName(name);
        if (trimmed.Length == 0) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
            .ToHashSet();

        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            if (char.IsControl(character) || invalid.Contains(character)) continue;
            builder.Append(character);
        }

        var result = builder.ToString().Trim();
        if (result is "." or "..") return string.Empty;
        if (result.Length > SaveGroupFormat.MaxNameLength)
            result = result[..SaveGroupFormat.MaxNameLength].TrimEnd();
        return result;
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

                if (!PathEquals(target, installationDirectory) && !IsUnder(target, installationDirectory))
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
            var entry = catalog.Installations.FirstOrDefault(candidate =>
                string.Equals(candidate.InstallationKey, GetInstallationKey(installation),
                    StringComparison.OrdinalIgnoreCase));
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
                ? $"当前存档已切换到「{group.Name}」；从 Steam 启动也会使用该组。"
                : $"「{group.Name}」已经是当前存档。",
            group);
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
            return Fail("这是当前存档组；请先选择并应用另一个组，再删除。");

        if (_options.Linker.Exists(_options.LiveSaveDirectory))
        {
            if (_options.Linker.TryGetTarget(_options.LiveSaveDirectory, out var liveTarget))
            {
                if (PathEquals(liveTarget, group.DirectoryPath))
                    return Fail("这是实际链接指向的存档组；请先切换到其他组，再删除。");
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
        catch (Exception exception)
        {
            // The directory already holds verified data and carries its manifest,
            // so Reconcile can rebuild the index later. Deleting it here would
            // destroy the only copy of an imported group.
            _options.Logger?.Warn(
                $"存档组「{name}」已写入 {finalDirectory}，但更新索引失败：{exception.Message}；刷新后会重新登记。");
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

    /// <summary>
    /// Moves the pre-import directory back to the live path. It reports failure
    /// instead of swallowing it: the caller may only delete the freshly imported
    /// copy when the user's original data is actually back in place.
    /// </summary>
    private BackupRestoreResult RestoreBackup(string backup, string live)
    {
        try
        {
            if (!Directory.Exists(backup))
                return new BackupRestoreResult(false, $"备份目录不存在：{backup}");

            if (Directory.Exists(live))
            {
                var info = new DirectoryInfo(live);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (!_options.Linker.RemoveJunction(live, out var removeError))
                        return new BackupRestoreResult(false, $"残留链接无法移除：{removeError}");
                }
                else if (Directory.EnumerateFileSystemEntries(live).Any())
                {
                    return new BackupRestoreResult(false, "目标路径已存在且非空，未覆盖。");
                }
                else
                {
                    Directory.Delete(live, recursive: false);
                }
            }

            Directory.Move(backup, live);
            _options.Logger?.Info($"原存档目录已恢复到 {live}");
            return new BackupRestoreResult(true, null);
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"恢复原存档目录失败：{exception.Message}");
            return new BackupRestoreResult(false, exception.Message);
        }
    }

    private sealed record BackupRestoreResult(bool Success, string? Error);

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

    /// <summary>True when the live link points into another installation's save
    /// folder. Auto-apply must not silently take it over; an explicit Apply may.</summary>
    private bool LiveLinkBelongsToAnotherInstallation(GameInstallation installation)
    {
        var live = _options.LiveSaveDirectory;
        if (!_options.Linker.Exists(live) || !_options.Linker.TryGetTarget(live, out var target))
            return false;

        var current = Path.GetFullPath(target);
        if (!IsUnder(current, ProfilesRoot)) return false;

        return !IsUnder(current, GetInstallationDirectory(installation));
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGroupDirectory(string directory) =>
        File.Exists(SaveGroupManifestStore.GetManifestPath(directory));

    private bool IsGameRunning(GameInstallation installation) =>
        _options.IsGameRunning?.Invoke(installation) == true;

    /// <summary>
    /// Serializes every mutating operation and re-probes the game process right
    /// before the body runs. A cached monitor snapshot is not good enough: the
    /// user can start the game from Steam between two 3-second scans, and the
    /// junction switch would then race the running game.
    /// </summary>
    private T Mutate<T>(GameInstallation installation, string action, Func<T> body, Func<string, T> fail)
    {
        lock (_writeGate)
        {
            var blocked = RunningBlockMessage(installation, action);
            return blocked is null ? body() : fail(blocked);
        }
    }

    private string? RunningBlockMessage(GameInstallation installation, string action)
    {
        var state = ProbeRunningState(installation);
        return state switch
        {
            GameRunningState.Running => $"游戏正在运行；请先关闭游戏再{action}。",
            GameRunningState.Unknown =>
                $"无法确认游戏是否正在运行（进程信息不可读）；为避免损坏存档，已停止{action}。请关闭游戏后重试。",
            _ => null
        };
    }

    private GameRunningState ProbeRunningState(GameInstallation installation)
    {
        try
        {
            if (_options.ProbeGameRunningState is not null)
                return _options.ProbeGameRunningState(installation);

            return IsGameRunning(installation)
                ? GameRunningState.Running
                : GameRunningState.NotRunning;
        }
        catch (Exception exception)
        {
            // A failed probe must never be read as "not running".
            _options.Logger?.Warn($"读取游戏运行状态失败：{exception.Message}");
            return GameRunningState.Unknown;
        }
    }

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
