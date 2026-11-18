using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Games;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.ViewModels;

public partial class SavesViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly SaveGroupService _saveGroups;
    private readonly SaveGroupArchiveService _archive;
    private readonly SaveSlotReader _slotReader;
    private readonly IFilePickerService _filePicker;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly GameProcessMonitor _processMonitor;
    private readonly GameExecutableVersionReader _gameVersions;

    // UI state is keyed by installation so a monitor refresh never loses the
    // user's selection or the expanded block.
    private bool _suppressSelectionSave;

    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    /// <summary>Global fact shown once at the top: the LocalLow junction points
    /// at exactly one group, and this says whose it is.</summary>
    [ObservableProperty]
    private string _liveOwnerInstanceName = string.Empty;

    [ObservableProperty]
    private string _liveOwnerGroupName = string.Empty;

    [ObservableProperty]
    private bool _hasLiveOwner;

    /// <summary>Normalized game directory of the version selected in the top
    /// instance picker. The view uses this to scroll the matching card into
    /// view when the active version changes.</summary>
    [ObservableProperty]
    private string? _activeInstallationKey;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True while any installation runs the game. The game writes the
    /// one global LocalLow directory, so this locks every save-write control on
    /// the page, not only the running installation.</summary>
    [ObservableProperty]
    private bool _anyRunning;

    public string RunningNotice =>
        "游戏正在运行；选择、克隆、导入、导出和删除已锁定，请关闭游戏后再操作。";

    public SavesViewModel(
        IAppShell shell,
        SaveGroupService saveGroups,
        SaveGroupArchiveService archive,
        SaveSlotReader slotReader,
        IFilePickerService filePicker,
        IUserDialogService dialogs,
        IAppLogger logger,
        GameProcessMonitor processMonitor,
        GameExecutableVersionReader gameVersions)
    {
        _shell = shell;
        _saveGroups = saveGroups;
        _archive = archive;
        _slotReader = slotReader;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _logger = logger;
        _processMonitor = processMonitor;
        _gameVersions = gameVersions;
        _shell.Game.Changed += (_, _) =>
        {
            // A transient row selection is not carried across instances: the
            // page always reopens on the live group of the newly active instance.
            ResetTransientState();
            _ = RefreshAsync();
        };

        // A process-state change only flips IsRunning: rebuilding the list here
        // would drop scroll position, expansion and the row selection.
        _processMonitor.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyRunningState);
    }

    public ObservableCollection<SaveInstallationItem> Instances { get; } = [];

    public Task OnActivatedAsync() => RefreshAsync();

    /// <summary>Selection is immediate: choosing a row writes the instance's
    /// stored group, and the active instance also switches the live link.</summary>
    public void ResetTransientState()
    {
        _suppressSelectionSave = true;
        try
        {
            foreach (var item in Instances)
            {
                foreach (var row in item.Groups)
                {
                    if (row.IsExpanded) row.IsExpanded = false;
                }

                item.SelectedGroup = item.CurrentGroup ?? item.Groups.FirstOrDefault();
                item.IsExpanded = item.IsActive;
            }
        }
        finally
        {
            _suppressSelectionSave = false;
        }
    }


    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RefreshAsync() =>
        // Keep the primary header button visually enabled. A click that lands
        // while a save operation is busy is ignored instead of starting a
        // competing refresh.
        IsBusy ? Task.CompletedTask : RefreshCoreAsync();

    /// <summary>One save operation at a time: the page blocks pointer input
    /// while <see cref="IsBusy"/> and every handler re-checks this flag, so a
    /// second click cannot start a competing import/export/apply.</summary>
    private bool TryBeginOperation()
    {
        if (!IsBusy) return true;
        _shell.SetStatus("正在处理另一个存档操作，请等待完成后再试。", StatusKind.Info);
        return false;
    }

    private async Task RefreshCoreAsync()
    {
        IsBusy = true;
        try
        {
            AnyRunning = _processMonitor.AnyRunning;
            var activePath = _shell.Game.Selected?.GameDirectory;
            var liveOwner = TryDescribeLiveOwner();
            var liveOwnerLabel = liveOwner?.Label;
            var desired = new List<SaveInstallationItem>();
            var existingByKey = new Dictionary<string, SaveInstallationItem>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var existing in Instances)
            {
                try
                {
                    existingByKey.TryAdd(
                        _saveGroups.GetInstallationKey(existing.Installation),
                        existing);
                }
                catch (Exception exception)
                {
                    _logger.Warn($"读取安装路径失败：{exception.Message}");
                }
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var installation in _shell.Game.Installations)
            {
                SaveInstallationItem? item = null;
                try
                {
                    var key = _saveGroups.GetInstallationKey(installation);
                    if (!seen.Add(key)) continue;
                    existingByKey.TryGetValue(key, out item);
                    var isActive = activePath is not null
                                   && SameDirectory(installation.GameDirectory, activePath);
                    var expandedOverride = item is not null && item.IsActive == isActive
                        ? item.IsExpanded
                        : (bool?)null;
                    var snapshot = await BuildInstallationSnapshotAsync(
                        installation,
                        activePath,
                        liveOwnerLabel,
                        expandedOverride);
                    if (item is null)
                    {
                        item = new SaveInstallationItem(
                            snapshot,
                            CreateEmptyAsync,
                            CloneAsync,
                            ImportAsync,
                            RememberExpansion,
                            SaveSelectionImmediately);
                    }
                    else
                    {
                        item.Apply(snapshot);
                    }

                    desired.Add(item);
                }
                catch (Exception exception)
                {
                    _logger.Warn($"读取存档组失败：{exception.Message}");
                    var message = $"读取存档组失败：{exception.Message}";
                    // Keep a card visible even when this installation is new and
                    // its first snapshot could not be built.
                    item ??= CreateUnavailableItem(installation, activePath, message);
                    item.NoticeMessage = message;
                    desired.Add(item);
                }
            }

            for (var index = Instances.Count - 1; index >= 0; index--)
            {
                if (!desired.Contains(Instances[index]))
                    Instances.RemoveAt(index);
            }

            for (var index = 0; index < desired.Count; index++)
            {
                var item = desired[index];
                var currentIndex = Instances.IndexOf(item);
                if (currentIndex < 0)
                    Instances.Insert(index, item);
                else if (currentIndex != index)
                    Instances.Move(currentIndex, index);
            }

            if (!SamePathOrNull(ActiveInstallationKey, activePath))
                ActiveInstallationKey = activePath;

            LiveOwnerInstanceName = liveOwner?.InstanceName ?? string.Empty;
            LiveOwnerGroupName = liveOwner?.GroupName ?? string.Empty;
            HasLiveOwner = liveOwner is not null;

            EmptyMessage = Instances.Count == 0
                ? "还没有添加游戏版本。先在设置中添加实例。"
                : string.Empty;
        }
        catch (Exception exception)
        {
            _logger.Error("刷新存档组失败", exception);
            _shell.SetStatus($"刷新存档组失败：{exception.Message}", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private SaveInstallationItem CreateUnavailableItem(
        GameInstallation installation,
        string? activePath,
        string message)
    {
        var isActive = false;
        try
        {
            isActive = activePath is not null && SameDirectory(installation.GameDirectory, activePath);
        }
        catch
        {
            // An invalid path is exactly what made the snapshot fail; keep the
            // card collapsed and let the notice explain the failure.
        }

        var snapshot = new SaveInstallationSnapshot(
            installation,
            isActive,
            AnyRunning,
            isActive,
            false,
            0,
            message,
            null,
            null,
            [],
            null,
            "未选择",
            string.Empty);

        return new SaveInstallationItem(
            snapshot,
            CreateEmptyAsync,
            CloneAsync,
            ImportAsync,
            RememberExpansion,
            SaveSelectionImmediately);
    }

    /// <summary>Resolves the global live link into the owning instance and group
    /// for the page header; null when it points at nothing (or is stale).</summary>
    private LiveOwnerDescription? TryDescribeLiveOwner()
    {
        try
        {
            var owner = _saveGroups.GetLiveOwner();
            if (owner is null) return null;

            var installation = _shell.Game.Installations.FirstOrDefault(candidate =>
                string.Equals(_saveGroups.GetInstallationKey(candidate), owner.InstallationKey,
                    StringComparison.OrdinalIgnoreCase));

            var name = installation?.DisplayLabel ?? owner.GameDirectory;
            return new LiveOwnerDescription(name, owner.Group.Name);
        }
        catch (Exception exception)
        {
            _logger.Warn($"读取当前存档链接失败：{exception.Message}");
            return null;
        }
    }

    private sealed record LiveOwnerDescription(string InstanceName, string GroupName)
    {
        public string Label => $"{InstanceName} · {GroupName}";
    }

    private async Task<SaveInstallationSnapshot> BuildInstallationSnapshotAsync(
        GameInstallation installation,
        string? activeGameDirectory,
        string? liveOwnerLabel,
        bool? expandedOverride)
    {
        // Two copies of the same edition share Profile.Id; only the exact game
        // directory is the active install.
        var isActive = activeGameDirectory is not null
                       && SameDirectory(installation.GameDirectory, activeGameDirectory);
        var isRunning = AnyRunning;

        IReadOnlyList<SaveGroup> groups = [];
        var needsUnmanagedChoice = false;
        var unmanagedFileCount = 0;
        var notice = string.Empty;
        try
        {
            groups = _saveGroups.GetGroups(installation);

            // Opening the page before the first launch must not hide an existing
            // single-profile save (nor let “new group” strand it). Initialize is
            // read-only for unmanaged saves and migrates a legacy profile without
            // touching the live junction; a running game is never written to.
            // When the link belongs to another copy, skip it: the header already
            // says which copy owns the link, and this is not an error.
            if (groups.Count == 0 && !isRunning && liveOwnerLabel is null)
            {
                var init = await _saveGroups.InitializeAsync(installation);
                if (init.State == SaveGroupInitializationState.NeedsUnmanagedChoice)
                {
                    needsUnmanagedChoice = true;
                    unmanagedFileCount = init.UnmanagedFileCount;
                    notice = $"检测到 {init.UnmanagedFileCount} 个未托管的存档文件。请先由 MK 启动一次游戏并选择“导入现存存档”或“新建空组”；在此之前不会复制或移动它们。";
                }
                else if (init.State == SaveGroupInitializationState.Failed)
                {
                    notice = init.Message;
                }
                else if (init.Success)
                {
                    groups = init.Groups;
                }
            }
        }
        catch (Exception exception)
        {
            // Keep the installation visible; the page can still create a group
            // once the underlying problem (permissions, locked file) is fixed.
            _logger.Warn($"读取存档组失败：{exception.Message}");
            notice = $"读取存档组失败：{exception.Message}";
        }

        SaveGroup? liveGroup = null;
        Guid? nextLaunchId = null;
        try
        {
            liveGroup = _saveGroups.GetLiveGroup(installation);
            nextLaunchId = _saveGroups.GetNextLaunchGroupId(installation);
        }
        catch (Exception exception)
        {
            _logger.Warn($"读取当前存档组失败：{exception.Message}");
        }

        var canDeleteGroup = groups.Count > 1;
        var rows = new List<SaveGroupRow>(groups.Count);
        foreach (var group in groups)
        {
            rows.Add(await BuildGroupRowAsync(
                installation,
                group,
                group.Id == liveGroup?.Id,
                group.Id == nextLaunchId,
                isRunning,
                canDeleteGroup));
        }

        var current = rows.FirstOrDefault(row => row.Id == liveGroup?.Id);

        var selected = rows.FirstOrDefault(row => row.Id == nextLaunchId)
                       ?? current
                       ?? rows.FirstOrDefault();

        var isExpanded = expandedOverride ?? isActive;

        // The group this copy will use next (catalog selection), independent of
        // which group currently owns the global LocalLow link.
        var chosen = rows.FirstOrDefault(row => row.Id == nextLaunchId);
        var selectedLabel = chosen?.Name
                            ?? rows.FirstOrDefault()?.Name
                            ?? "未选择";
        var linkOwner = current is null && liveOwnerLabel is not null ? liveOwnerLabel : string.Empty;

        return new SaveInstallationSnapshot(
            installation,
            isActive,
            isRunning,
            isExpanded,
            needsUnmanagedChoice,
            unmanagedFileCount,
            notice,
            current,
            chosen,
            rows,
            selected,
            selectedLabel,
            linkOwner);
    }

    private async Task<SaveGroupRow> BuildGroupRowAsync(
        GameInstallation installation,
        SaveGroup group,
        bool isCurrent,
        bool isNextLaunch,
        bool isRunning,
        bool canDeleteGroup)
    {
        SaveGroupDetails details;
        try
        {
            // Hashing and gzip parsing are bounded but still file I/O; keep them
            // off the UI thread so a large group cannot freeze the page.
            details = await Task.Run(
                () => _slotReader.ReadGroup(group.DirectoryPath, installation.GameDirectory));
        }
        catch (Exception exception)
        {
            _logger.Warn($"读取存档组文件失败：{exception.Message}");
            details = new SaveGroupDetails
            {
                Error = $"读取存档组失败：{exception.Message}"
            };
        }

        return new SaveGroupRow(
            installation,
            group,
            isCurrent,
            isNextLaunch,
            isRunning,
            details,
            canDeleteGroup,
            OpenGroupAsync,
            ExportGroupAsync,
            RenameGroupAsync,
            DeleteGroupAsync);
    }

    /// <summary>Updates only the running flags; list items, expansion, scroll
    /// and the row selection are left untouched.</summary>
    private void ApplyRunningState()
    {
        var running = _processMonitor.AnyRunning;
        AnyRunning = running;

        foreach (var item in Instances)
        {
            item.IsRunning = running;
            foreach (var row in item.Groups) row.IsRunning = running;
        }
    }

    private static bool SameDirectory(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool SamePathOrNull(string? left, string? right) =>
        left is null || right is null
            ? left is null && right is null
            : SameDirectory(left, right);

    private void RememberExpansion(SaveInstallationItem item)
    {
        if (!item.IsExpanded) return;

        // Installation cards behave like an accordion: only one block stays
        // open. The card's own IsExpanded value is the single source of truth.
        foreach (var other in Instances)
        {
            if (!ReferenceEquals(other, item) && other.IsExpanded)
                other.IsExpanded = false;
        }
    }

    private void SaveSelectionImmediately(SaveInstallationItem item)
    {
        if (_suppressSelectionSave) return;
        _ = SaveSelectionAsync(item);
    }

    private async Task SaveSelectionAsync(SaveInstallationItem item)
    {
        if (IsBusy || !item.HasPending) return;

        var group = item.SelectedGroup!;
        IsBusy = true;
        try
        {
            var result = await ApplyItemAsync(item);
            var message = result.Success && !item.IsActive
                ? $"已更新「{item.Name}」的选定组为「{group.Name}」。"
                : result.Message;
            _shell.SetStatus(message, result.Success ? StatusKind.Success : StatusKind.Error);

            // A non-active copy only rewrites its stored choice: update that
            // card in place so the list, expansion and scroll stay put. Active
            // copies change the live LocalLow link and still need a refresh.
            if (result.Success && !item.IsActive)
                item.AdoptChosenGroup(group);
            else
                await RefreshCoreAsync();
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"保存存档组失败：{exception.Message}", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task OpenGroupAsync(SaveGroupRow row)
    {
        try
        {
            if (!Directory.Exists(row.DirectoryPath))
            {
                _shell.SetStatus("该存档组目录还不存在；首次用该组启动游戏后会创建。", StatusKind.Info);
                return Task.CompletedTask;
            }

            Process.Start(new ProcessStartInfo(row.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开存档组目录失败：{exception.Message}", StatusKind.Error);
        }

        return Task.CompletedTask;
    }

    private async Task ExportGroupAsync(SaveGroupRow row)
    {
        if (row.IsRunning)
        {
            _shell.SetStatus("游戏正在运行，先关闭游戏再备份或导出存档组。", StatusKind.Warning);
            return;
        }

        var suggested = PathSafety.GetSafeFileName($"{row.Installation.DisplayLabel}-{row.Name}");
        var target = await _filePicker.PickSaveArchiveTargetAsync(
            $"{suggested}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (string.IsNullOrWhiteSpace(target)) return;

        IsBusy = true;
        try
        {
            var result = await _archive.ExportAsync(
                row.Group,
                SaveGameInfo.FromInstallation(row.Installation, _gameVersions.ReadVersion(row.Installation)),
                target);
            _shell.SetStatus(
                result.Success ? $"{result.Message} 已保存到 {result.ArchivePath}" : result.Message,
                result.Success ? StatusKind.Success : StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RenameGroupAsync(SaveGroupRow row)
    {
        if (row.IsRunning)
        {
            _shell.SetStatus("游戏正在运行，先关闭游戏再重命名存档组。", StatusKind.Warning);
            return;
        }

        if (!TryBeginOperation()) return;

        var input = await _dialogs.PromptAsync(
            "重命名存档组",
            $"重命名「{row.Name}」只改名称；不会移动、复制或删除存档文件。",
            row.Name,
            "存档组名称");
        if (input is null) return;

        IsBusy = true;
        try
        {
            var result = await _saveGroups.RenameAsync(row.Installation, row.Id, input);
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private async Task DeleteGroupAsync(SaveGroupRow row)
    {
        if (row.IsRunning)
        {
            _shell.SetStatus("游戏正在运行，先关闭游戏再删除存档组。", StatusKind.Warning);
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "删除存档组",
            $"将「{row.Name}」移入 Windows 回收站。\n\n" +
            "不会永久删除存档；可以从回收站还原。",
            "删除：移入回收站",
            "取消");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = await _saveGroups.DeleteGroupAsync(row.Installation, row.Id);
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            if (result.Success) await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>One apply, in the mode that matches this copy: the active copy
    /// switches the global LocalLow link; any other copy only writes its own
    /// stored NextLaunchGroupId flag.</summary>
    private async Task<SaveGroupOperationResult> ApplyItemAsync(SaveInstallationItem item)
    {
        if (item.SelectedGroup is not { } group)
            return new SaveGroupOperationResult(false, "请先点击一个存档组。");

        var result = item.IsActive
            ? await _saveGroups.ApplyCurrentAsync(item.Installation, group.Id)
            : await _saveGroups.SelectNextLaunchAsync(item.Installation, group.Id);

        return result;
    }

    private async Task CreateEmptyAsync(SaveInstallationItem item)
    {
        if (item.IsRunning)
        {
            _shell.SetStatus("游戏正在运行，先关闭游戏再新建存档组。", StatusKind.Warning);
            return;
        }

        if (item.NeedsUnmanagedChoice)
        {
            _shell.SetStatus(
                "检测到未托管的存档；请先由 MK 启动一次游戏，选择导入现存存档或新建空组。未改动任何文件。",
                StatusKind.Warning);
            return;
        }

        if (!TryBeginOperation()) return;

        var suggestion = _saveGroups.SuggestGroupName(
            item.Installation,
            $"存档组 {item.Groups.Count + 1}");
        item.IsExpanded = true;
        var input = await _dialogs.PromptAsync(
            "新建空存档组",
            "创建空的存档目录；不会复制当前组，也不会更改 Mod 组。",
            suggestion,
            "存档组名称");
        if (input is null) return;

        IsBusy = true;
        try
        {
            var result = await _saveGroups.CreateEmptyAsync(item.Installation, input);
            if (!result.Success)
            {
                _shell.SetStatus(result.Message, StatusKind.Error);
                return;
            }

            await FinishGroupCreationAsync(item.Installation, result.Group);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CloneAsync(SaveInstallationItem item)
    {
        if (item.IsRunning)
        {
            _shell.SetStatus("游戏正在运行，先关闭游戏再克隆存档组。", StatusKind.Warning);
            return;
        }

        if (item.NeedsUnmanagedChoice)
        {
            _shell.SetStatus(
                "检测到未托管的存档；请先由 MK 启动一次游戏，选择导入现存存档或新建空组。未改动任何文件。",
                StatusKind.Warning);
            return;
        }

        var source = item.SelectedGroup ?? item.CurrentGroup ?? item.Groups.FirstOrDefault();
        if (source is null)
        {
            _shell.SetStatus("还没有可克隆的存档组；先新建一个空组。", StatusKind.Warning);
            return;
        }

        if (!TryBeginOperation()) return;

        var suggestion = _saveGroups.SuggestGroupName(item.Installation, $"{source.Name} 副本");
        item.IsExpanded = true;
        var input = await _dialogs.PromptAsync(
            "克隆存档组",
            $"复制「{source.Name}」的完整存档目录到新组；新组使用新的 ID，源组保持不变。",
            suggestion,
            "新存档组名称");
        if (input is null) return;

        IsBusy = true;
        try
        {
            var result = await _saveGroups.CloneAsync(item.Installation, source.Id, input);
            if (!result.Success)
            {
                _shell.SetStatus(result.Message, StatusKind.Error);
                return;
            }

            await FinishGroupCreationAsync(item.Installation, result.Group);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAsync(SaveInstallationItem item)
    {
        if (item.IsRunning)
        {
            _shell.SetStatus("游戏正在运行，先关闭游戏再导入存档组。", StatusKind.Warning);
            return;
        }

        if (item.NeedsUnmanagedChoice)
        {
            _shell.SetStatus(
                "检测到未托管的存档；请先由 MK 启动一次游戏，选择导入现存存档或新建空组。未改动任何文件。",
                StatusKind.Warning);
            return;
        }

        if (!TryBeginOperation()) return;

        var zip = await _filePicker.PickSaveArchiveAsync();
        if (string.IsNullOrWhiteSpace(zip)) return;

        var inspection = await _archive.InspectAsync(zip);
        if (!inspection.Success || inspection.Manifest is null)
        {
            _shell.SetStatus($"无法导入：{inspection.Message}", StatusKind.Error);
            return;
        }

        var manifest = inspection.Manifest;
        var targetVersion = _gameVersions.ReadVersion(item.Installation);
        var name = await _dialogs.PromptAsync(
            "导入存档组",
            $"来源：{manifest.GroupName}（{manifest.SourceEdition} · {manifest.SourceVersion}）\n" +
            "导入会新建一个独立存档组，不覆盖或合并现有组，也不会修改源 ZIP。",
            _saveGroups.SuggestGroupName(item.Installation, manifest.GroupName),
            "新存档组名称");
        if (name is null) return;

        IsBusy = true;
        try
        {
            var allowMismatch = false;
            for (var attempt = 0; attempt < 5; attempt++)
        {
                var result = await _archive.ImportAsync(
                    zip,
                    item.Installation,
                    _saveGroups,
                    name,
                    allowMismatch,
                    targetVersion);

                if (result.State == SaveGroupImportState.NeedsEditionConfirmation)
            {
                    var confirmed = await _dialogs.ConfirmAsync(
                        "来源版本不一致",
                        result.Message + "\n\n仍要导入到一个新存档组吗？",
                        "仍然导入",
                        "取消");
                    if (!confirmed)
                {
                        _shell.SetStatus("已取消导入；没有创建或修改任何存档组。", StatusKind.Info);
                        return;
                    }

                    allowMismatch = true;
                    continue;
                }

                if (!result.Success && result.SuggestedName is not null)
            {
                    var retry = await _dialogs.PromptAsync(
                        "存档组名称已存在",
                        result.Message,
                        result.SuggestedName,
                        "新存档组名称");
                    if (retry is null) return;
                    name = retry;
                    continue;
                }

                if (!result.Success)
            {
                    _shell.SetStatus(result.Message, StatusKind.Error);
                    return;
                }

                await FinishGroupCreationAsync(item.Installation, result.Group);
                return;
            }

            _shell.SetStatus("导入未完成：名称多次冲突或确认未完成；未创建新组。", StatusKind.Warning);
            }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Create/clone/import never ask whether to replace the current group. The
    /// only exception is the first group of an installation, which is applied
    /// so a fresh setup is immediately usable.
    /// </summary>
    private async Task FinishGroupCreationAsync(GameInstallation installation, SaveGroup? group)
    {
        var applied = false;
        if (group is not null)
        {
            try
            {
                // The service applies the first group itself (it holds the write
                // lock); for later groups the live link intentionally stays put.
                applied = _saveGroups.GetLiveGroup(installation)?.Id == group.Id;
            }
            catch (Exception exception)
            {
                _logger.Warn($"读取当前存档组失败：{exception.Message}");
            }
        }

        await RefreshCoreAsync();
        if (group is null) return;

        _shell.SetStatus(
            applied
                ? $"「{group.Name}」已创建，并作为当前存档。"
                : $"「{group.Name}」已创建；当前存档保持不变。",
            applied ? StatusKind.Success : StatusKind.Info);
    }
}
