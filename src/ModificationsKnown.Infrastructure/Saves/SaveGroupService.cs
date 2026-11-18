using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// Owns save-group data: the catalog, per-group manifests, legacy profile
/// migration, first-run handling of unmanaged saves, and the launch-time
/// junction switch. All filesystem roots and the linker are injectable so tests
/// never touch the player's real LocalLow or AppData directories.
/// </summary>
public sealed partial class SaveGroupService
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
            var entry = FindCatalogEntry(catalog, installation);
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

    /// <summary>The catalog entry for an installation, or null when the index has
    /// no record for it (a scan rebuilds it from the manifests).</summary>
    private SaveGroupInstallationEntry? FindCatalogEntry(
        SaveGroupCatalog catalog,
        GameInstallation installation)
    {
        var key = GetInstallationKey(installation);
        return catalog.Installations.FirstOrDefault(candidate =>
            string.Equals(candidate.InstallationKey, key, StringComparison.OrdinalIgnoreCase));
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
        var entry = FindCatalogEntry(catalog, installation);

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
            var blocked = RunningBlock(
                installation,
                () => $"游戏正在运行；请先关闭游戏再{action}。",
                () => $"无法确认游戏是否正在运行（进程信息不可读）；为避免损坏存档，已停止{action}。请关闭游戏后重试。");
            return blocked is null ? body() : fail(blocked);
        }
    }

    /// <summary>
    /// The single fail-closed running-state gate for writes: only a confirmed
    /// <see cref="GameRunningState.NotRunning"/> may proceed, and
    /// <see cref="GameRunningState.Unknown"/> is treated exactly like Running.
    /// Callers keep their own wording so the message still names the action.
    /// </summary>
    private string? RunningBlock(
        GameInstallation installation,
        Func<string> whenRunning,
        Func<string> whenUnknown) =>
        ProbeRunningState(installation) switch
        {
            GameRunningState.Running => whenRunning(),
            GameRunningState.Unknown => whenUnknown(),
            _ => null
        };

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
