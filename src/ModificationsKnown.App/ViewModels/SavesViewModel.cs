using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.ViewModels;

public sealed record SaveFileRow(string RelativePath, string SizeLabel);

/// <summary>One compact save-group row: identity, the two state tags, file
/// metrics and the per-group actions. Rebuilt on every refresh, so it never
/// holds stale selection state.</summary>
public sealed class SaveGroupRow
{
    public SaveGroupRow(
        GameInstallation installation,
        SaveGroup group,
        bool isNextLaunch,
        bool isRunning,
        IReadOnlyList<SaveFileRow> files,
        string sizeLabel,
        string updatedLabel,
        Func<SaveGroupRow, Task> open,
        Func<SaveGroupRow, Task> rename,
        Func<SaveGroupRow, Task> selectNextLaunch)
    {
        Installation = installation;
        Group = group;
        IsNextLaunch = isNextLaunch;
        IsRunning = isRunning;
        Files = files;
        SizeLabel = sizeLabel;
        UpdatedLabel = updatedLabel;

        OpenCommand = new AsyncRelayCommand(() => open(this));
        RenameCommand = new AsyncRelayCommand(() => rename(this));
        SelectNextLaunchCommand = new AsyncRelayCommand(() => selectNextLaunch(this));
    }

    public GameInstallation Installation { get; }

    public SaveGroup Group { get; }

    public Guid Id => Group.Id;

    public string Name => Group.Name;

    public bool IsDefault => Group.IsDefault;

    public bool IsNextLaunch { get; }

    public bool IsRunning { get; }

    public bool CanEdit => !IsRunning;

    public bool CanSelectNextLaunch => !IsRunning && !IsNextLaunch;

    public string DirectoryPath => Group.DirectoryPath;

    public IReadOnlyList<SaveFileRow> Files { get; }

    public bool IsEmpty => Files.Count == 0;

    public string SummaryLabel => $"{Files.Count} 个文件 · {SizeLabel} · 最后更新 {UpdatedLabel}";

    public string SizeLabel { get; }

    public string UpdatedLabel { get; }

    public IAsyncRelayCommand OpenCommand { get; }

    public IAsyncRelayCommand RenameCommand { get; }

    public IAsyncRelayCommand SelectNextLaunchCommand { get; }
}

/// <summary>One collapsible installation block on the saves page.</summary>
public partial class SaveInstallationItem : ObservableObject
{
    private readonly Guid? _nextLaunchGroupId;
    private readonly Action<SaveInstallationItem> _expandedChanged;

    public SaveInstallationItem(
        GameInstallation installation,
        bool isActive,
        bool isRunning,
        bool isExpanded,
        bool needsUnmanagedChoice,
        int unmanagedFileCount,
        string noticeMessage,
        IReadOnlyList<SaveGroupRow> groups,
        Guid? nextLaunchGroupId,
        Func<SaveInstallationItem, Task> createEmpty,
        Func<SaveInstallationItem, Task> clone,
        Func<SaveInstallationItem, Task> commitSelection,
        Action<SaveInstallationItem> expandedChanged)
    {
        Installation = installation;
        IsActive = isActive;
        IsRunning = isRunning;
        NeedsUnmanagedChoice = needsUnmanagedChoice;
        UnmanagedFileCount = unmanagedFileCount;
        NoticeMessage = noticeMessage;
        _nextLaunchGroupId = nextLaunchGroupId;
        _expandedChanged = expandedChanged;

        foreach (var group in groups) Groups.Add(group);

        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        CreateEmptyCommand = new AsyncRelayCommand(() => createEmpty(this));
        CloneCommand = new AsyncRelayCommand(() => clone(this));
        CommitSelectionCommand = new AsyncRelayCommand(
            () => commitSelection(this),
            () => CanCommitSelection);

        _isExpanded = isExpanded;
        SelectedNextGroup = NextLaunchGroup;
    }

    public GameInstallation Installation { get; }

    public string Name => Installation.DisplayLabel;

    public string GamePath => Installation.GameDirectory;

    public bool IsActive { get; }

    public bool NeedsUnmanagedChoice { get; }

    public int UnmanagedFileCount { get; }

    public string NoticeMessage { get; }

    public bool HasNotice => NoticeMessage.Length > 0;

    public ObservableCollection<SaveGroupRow> Groups { get; } = [];

    public SaveGroupRow? NextLaunchGroup =>
        Groups.FirstOrDefault(group => group.Id == _nextLaunchGroupId);

    public SaveGroupRow? NextLaunchGroupOrFirst => NextLaunchGroup ?? Groups.FirstOrDefault();

    public string NextLaunchLabel => NextLaunchGroup?.Name ?? "未创建";

    public string GroupCountLabel => $"{Groups.Count} 个存档组";

    public bool HasGroups => Groups.Count > 0;

    public bool HasNoGroups => Groups.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded;

    [ObservableProperty]
    private SaveGroupRow? _selectedGroup;

    [ObservableProperty]
    private SaveGroupRow? _selectedNextGroup;

    [ObservableProperty]
    private bool _isRunning;

    public string Chevron => IsExpanded ? "▾" : "▸";

    public bool CanEdit => !IsRunning && !NeedsUnmanagedChoice;

    public bool CanCommitSelection =>
        CanEdit
        && SelectedNextGroup is not null
        && SelectedNextGroup.Id != _nextLaunchGroupId;

    public IRelayCommand ToggleExpandedCommand { get; }

    public IAsyncRelayCommand CreateEmptyCommand { get; }

    public IAsyncRelayCommand CloneCommand { get; }

    public IAsyncRelayCommand CommitSelectionCommand { get; }

    partial void OnIsExpandedChanged(bool value) => _expandedChanged(this);

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanCommitSelection));
        CommitSelectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedNextGroupChanged(SaveGroupRow? value)
    {
        OnPropertyChanged(nameof(CanCommitSelection));
        CommitSelectionCommand.NotifyCanExecuteChanged();
    }
}

public partial class SavesViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly SaveGroupService _saveGroups;
    private readonly IUserDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly GameProcessMonitor _processMonitor;

    // Expansion is per installation (not per rebuild) so a process-monitor
    // refresh does not collapse the block the user is working in.
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public SavesViewModel(
        IAppShell shell,
        SaveGroupService saveGroups,
        IUserDialogService dialogs,
        IAppLogger logger,
        GameProcessMonitor processMonitor)
    {
        _shell = shell;
        _saveGroups = saveGroups;
        _dialogs = dialogs;
        _logger = logger;
        _processMonitor = processMonitor;
        _shell.Game.Changed += (_, _) => _ = RefreshAsync();
        _processMonitor.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    }

    public ObservableCollection<SaveInstallationItem> Instances { get; } = [];

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync();

    private async Task RefreshCoreAsync()
    {
        IsBusy = true;
        try
        {
            var activeId = _shell.Game.Selected?.Profile.Id;
            var items = new List<SaveInstallationItem>();
            foreach (var installation in _shell.Game.Installations)
            {
                try
                {
                    items.Add(await BuildInstallationItemAsync(installation, activeId));
                }
                catch (Exception exception)
                {
                    _logger.Warn($"读取存档组失败：{exception.Message}");
                }
            }

            Instances.Clear();
            foreach (var item in items) Instances.Add(item);

            EmptyMessage = Instances.Count == 0
                ? "还没有添加游戏版本。先在设置中添加实例。"
                : string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<SaveInstallationItem> BuildInstallationItemAsync(
        GameInstallation installation,
        string? activeId)
    {
        var isActive = string.Equals(installation.Profile.Id, activeId, StringComparison.OrdinalIgnoreCase);
        var isRunning = _processMonitor.IsRunning(installation);
        var key = _saveGroups.GetInstallationKey(installation);
        var rows = new List<SaveGroupRow>();

        IReadOnlyList<SaveGroup> groups = [];
        Guid? nextLaunchId = null;
        var needsUnmanagedChoice = false;
        var unmanagedFileCount = 0;
        var notice = string.Empty;
        try
        {
            groups = _saveGroups.GetGroups(installation);
            nextLaunchId = _saveGroups.GetNextLaunchGroupId(installation);

            // Opening the page before the first launch must not hide an existing
            // single-profile save (nor let “new group” strand it). Initialize is
            // read-only for unmanaged saves and migrates a legacy profile without
            // touching the live junction; a running game is never written to.
            if (groups.Count == 0 && !isRunning)
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
                    nextLaunchId = init.NextLaunchGroupId;
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

        foreach (var group in groups)
            rows.Add(BuildGroupRow(installation, group, group.Id == nextLaunchId, isRunning));

        var isExpanded = _expansion.TryGetValue(key, out var stored) ? stored : isActive;
        return new SaveInstallationItem(
            installation,
            isActive,
            isRunning,
            isExpanded,
            needsUnmanagedChoice,
            unmanagedFileCount,
            notice,
            rows,
            nextLaunchId,
            CreateEmptyAsync,
            CloneAsync,
            CommitSelectionAsync,
            RememberExpansion);
    }

    private SaveGroupRow BuildGroupRow(
        GameInstallation installation,
        SaveGroup group,
        bool isNextLaunch,
        bool isRunning)
    {
        var files = new List<SaveFileRow>();
        var total = 0L;
        DateTime? updated = null;

        try
        {
            if (Directory.Exists(group.DirectoryPath))
            {
                foreach (var path in Directory.EnumerateFiles(
                             group.DirectoryPath, "*", SearchOption.AllDirectories))
                {
                    if (SaveGroupFormat.IsInternalFile(Path.GetFileName(path))) continue;

                    var info = new FileInfo(path);
                    files.Add(new SaveFileRow(
                        Path.GetRelativePath(group.DirectoryPath, path),
                        FormatSize(info.Length)));
                    total += info.Length;
                    if (updated is null || info.LastWriteTime > updated) updated = info.LastWriteTime;
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Warn($"读取存档组文件失败：{exception.Message}");
        }

        return new SaveGroupRow(
            installation,
            group,
            isNextLaunch,
            isRunning,
            files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            FormatSize(total),
            updated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—",
            OpenGroupAsync,
            RenameGroupAsync,
            SelectNextLaunchAsync);
    }

    private void RememberExpansion(SaveInstallationItem item) =>
        _expansion[_saveGroups.GetInstallationKey(item.Installation)] = item.IsExpanded;

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

    private async Task RenameGroupAsync(SaveGroupRow row)
    {
        if (row.IsRunning)
        {
            _shell.SetStatus("游戏正在运行，先关闭游戏再重命名存档组。", StatusKind.Warning);
            return;
        }

        var input = await _dialogs.PromptAsync(
            "重命名存档组",
            $"重命名「{row.Name}」只改名称；不会移动、复制或删除存档文件。",
            row.Name,
            "存档组名称");
        if (input is null) return;

        var result = await _saveGroups.RenameAsync(row.Installation, row.Id, input);
        _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
        if (result.Success) await RefreshAsync();
    }

    private async Task SelectNextLaunchAsync(SaveGroupRow row)
    {
        var result = await _saveGroups.SelectNextLaunchAsync(row.Installation, row.Id);
        _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
        if (result.Success) await RefreshAsync();
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

        var suggestion = _saveGroups.SuggestGroupName(
            item.Installation,
            $"存档组 {item.Groups.Count + 1}");
        item.IsExpanded = true;
        var input = await _dialogs.PromptAsync(
            "新建空存档组",
            "创建空的 LocalLow 存档目录；不会复制当前组，也不会更改 Mod 组。",
            suggestion,
            "存档组名称");
        if (input is null) return;

        var result = await _saveGroups.CreateEmptyAsync(item.Installation, input);
        if (!result.Success)
        {
            _shell.SetStatus(result.Message, StatusKind.Error);
            return;
        }

        _shell.SetStatus(result.Message, StatusKind.Success);
        await RefreshAsync();
        if (result.Group is not null)
            await OfferAsNextLaunchAsync(item.Installation, result.Group, "存档组已创建");
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

        var source = item.SelectedNextGroup ?? item.NextLaunchGroupOrFirst;
        if (source is null)
        {
            _shell.SetStatus("还没有可克隆的存档组；先新建一个空组。", StatusKind.Warning);
            return;
        }

        var suggestion = _saveGroups.SuggestGroupName(item.Installation, $"{source.Name} 副本");
        item.IsExpanded = true;
        var input = await _dialogs.PromptAsync(
            "克隆当前存档组",
            $"复制「{source.Name}」的完整存档目录到新组；新组使用新的 ID，源组保持不变。",
            suggestion,
            "新存档组名称");
        if (input is null) return;

        var result = await _saveGroups.CloneAsync(item.Installation, source.Id, input);
        if (!result.Success)
        {
            _shell.SetStatus(result.Message, StatusKind.Error);
            return;
        }

        _shell.SetStatus(result.Message, StatusKind.Success);
        await RefreshAsync();
        if (result.Group is not null)
            await OfferAsNextLaunchAsync(item.Installation, result.Group, "存档组已克隆");
    }

    private async Task CommitSelectionAsync(SaveInstallationItem item)
    {
        if (item.SelectedNextGroup is not { } group)
        {
            _shell.SetStatus("请先在下拉框中选择一个存档组。", StatusKind.Info);
            return;
        }

        if (item.IsRunning)
        {
            _shell.SetStatus("游戏正在运行，先关闭游戏再选择下次启动的存档组。", StatusKind.Warning);
            return;
        }

        var result = await _saveGroups.SelectNextLaunchAsync(item.Installation, group.Id);
        _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
        if (result.Success) await RefreshAsync();
    }

    /// <summary>
    /// After a create/clone the user decides whether the new group becomes the
    /// next launch group. The first group is already selected by the service, so
    /// there is nothing to ask.
    /// </summary>
    private async Task OfferAsNextLaunchAsync(
        GameInstallation installation,
        SaveGroup group,
        string title)
    {
        if (_saveGroups.GetNextLaunchGroupId(installation) == group.Id)
        {
            _shell.SetStatus(
                $"「{group.Name}」已就绪，并作为下次 MK 启动使用的存档组。",
                StatusKind.Success);
            await RefreshAsync();
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            title,
            $"是否将「{group.Name}」设为下次由 MK 启动时使用？",
            "设为下次启动",
            "保持现有选择");
        if (!confirmed)
        {
            _shell.SetStatus($"已保留「{group.Name}」；下次启动组保持不变。", StatusKind.Info);
            return;
        }

        var result = await _saveGroups.SelectNextLaunchAsync(installation, group.Id);
        _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
        if (result.Success) await RefreshAsync();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };
}
