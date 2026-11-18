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
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.ViewModels;

public sealed record SaveFact(string Text, string? Tip = null);

/// <summary>One save inside a group, formatted for the detail cards.</summary>
public sealed class SaveSlotCard
{
    public SaveSlotCard(SaveSlotMetadata save)
    {
        Save = save;
        Label = string.IsNullOrWhiteSpace(save.Label) ? save.FileName : save.Label;
        Badges = BuildBadges(save);
        Facts = BuildFacts(save);
        FileLine = $"{save.FileName} · {FormatSize(save.FileSize)}";
    }

    public SaveSlotMetadata Save { get; }

    public string Label { get; }

    public IReadOnlyList<string> Badges { get; }

    public IReadOnlyList<SaveFact> Facts { get; }

    public string FileLine { get; }

    public bool IsReadable => Save.IsReadable;

    public bool IsUnreadable => !Save.IsReadable;

    public bool HasError => !string.IsNullOrWhiteSpace(Save.Error);

    public string Error => Save.Error ?? string.Empty;

    private static IReadOnlyList<string> BuildBadges(SaveSlotMetadata save)
    {
        var badges = new List<string> { KindLabel(save.Kind) };
        if (save.IsMostRecent) badges.Add("最近");
        if (save.IsMultiplayer) badges.Add("多人");
        if (save.Cheated) badges.Add("已修改");
        if (save.ModCount > 0) badges.Add($"模组 {save.ModCount}");
        return badges;
    }

    private static IReadOnlyList<SaveFact> BuildFacts(SaveSlotMetadata save)
    {
        var facts = new List<SaveFact>();

        // 深度置顶：LocationName 里的“2158m Deep”与深度是同一份数据，只保留深度。
        if (save.DepthMeters is > 0) facts.Add(new($"深度：{save.DepthMeters:0} m"));
        if (!string.IsNullOrWhiteSpace(save.LayerName)) facts.Add(new($"层级：{save.LayerName}"));
        else if (save.LayerIndex is { } layer) facts.Add(new($"层级：第 {layer + 1} 层"));
        if (!string.IsNullOrWhiteSpace(save.DifficultyLabel))
        {
            var tip = string.Equals(save.DifficultyKey, SaveDifficultyPresets.CustomKey, StringComparison.Ordinal)
                ? SaveDifficultyPresets.CustomDescription
                : null;
            facts.Add(new($"难度：{save.DifficultyLabel}", tip));
        }
        if (!string.IsNullOrWhiteSpace(save.CharacterLabel)) facts.Add(new($"角色：{save.CharacterLabel}"));
        if (!string.IsNullOrWhiteSpace(save.RunTimeLabel)) facts.Add(new($"游玩：{save.RunTimeLabel}"));
        if (!string.IsNullOrWhiteSpace(save.SavedAtLabel)) facts.Add(new($"保存：{save.SavedAtLabel}"));
        if (save.CharacterId is { } characterId) facts.Add(new($"角色 ID：{characterId}"));
        if (save.SeedValue is { } seed) facts.Add(new($"种子：{seed}"));
        if (!string.IsNullOrWhiteSpace(save.SaveKind)) facts.Add(new($"类型：{save.SaveKind}"));
        if (facts.Count == 0 && save.IsReadable) facts.Add(new("无更多元数据"));
        return facts;
    }

    private static string KindLabel(SaveArtifactKind kind) => kind switch
    {
        SaveArtifactKind.Slot => "槽位",
        SaveArtifactKind.Current => "当前进度",
        SaveArtifactKind.Autosave => "自动存档",
        SaveArtifactKind.SingleSave => "原版存档",
        _ => "文件"
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };
}

/// <summary>One compact save-group row. Clicking the row selects it; the
/// chevron is a second, explicit click that expands the save cards.</summary>
public sealed partial class SaveGroupRow : ObservableObject
{
    private readonly bool _canDeleteGroup;

    public SaveGroupRow(
        GameInstallation installation,
        SaveGroup group,
        bool isCurrent,
        bool isNextLaunch,
        bool isRunning,
        SaveGroupDetails details,
        bool canDeleteGroup,
        Func<SaveGroupRow, Task> open,
        Func<SaveGroupRow, Task> export,
        Func<SaveGroupRow, Task> rename,
        Func<SaveGroupRow, Task> delete)
    {
        Installation = installation;
        Group = group;
        IsCurrent = isCurrent;
        IsNextLaunch = isNextLaunch;
        _isRunning = isRunning;
        _canDeleteGroup = canDeleteGroup;
        Details = details;
        Saves = details.Saves.Select(save => new SaveSlotCard(save)).ToList();

        OpenCommand = new AsyncRelayCommand(() => open(this));
        ExportCommand = new AsyncRelayCommand(() => export(this), () => CanExport);
        RenameCommand = new AsyncRelayCommand(() => rename(this), () => CanEdit);
        DeleteCommand = new AsyncRelayCommand(() => delete(this), () => CanDelete);
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        SelectCommand = new RelayCommand(
            () => Selected?.Invoke(this, EventArgs.Empty),
            () => CanSelect);
    }

    public GameInstallation Installation { get; }

    public SaveGroup Group { get; }

    public Guid Id => Group.Id;

    public string Name => Group.Name;

    public bool IsCurrent { get; }

    public bool IsNextLaunch { get; }

    public bool IsDefault => Group.IsDefault;

    public bool HasCurrentTag => IsCurrent;

    public string CurrentTag => IsCurrent ? "当前" : string.Empty;

    public string DirectoryPath => Group.DirectoryPath;

    public SaveGroupDetails Details { get; }

    public IReadOnlyList<SaveSlotCard> Saves { get; }

    public bool HasSaves => Saves.Count > 0;

    public bool IsEmpty => Saves.Count == 0;

    public string SummaryLabel => Details.SummaryLabel;



    public bool HasError => !string.IsNullOrWhiteSpace(Details.Error);

    public string DetailError => Details.Error ?? string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectLabel))]
    [NotifyPropertyChangedFor(nameof(SelectTip))]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private bool _isPending;

    /// <summary>True while another group of the same installation is pending.
    /// The live group keeps a read-only “当前应用” label until then, and turns
    /// into a real “选择存档组” button so the pending choice can be undone
    /// without applying it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectLabel))]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    [NotifyPropertyChangedFor(nameof(SelectTip))]
    private bool _hasPendingSelection;


    /// <summary>True for the row chosen with “选择存档组”; drives the accent border.</summary>
    [ObservableProperty]
    private bool _isSelected;


    /// <summary>Raised by the row's “选择存档组” button; the page marks it pending.</summary>
    public event EventHandler? Selected;

    /// <summary>Raised when <see cref="IsExpanded"/> changes; the page keeps one row open.</summary>
    public event EventHandler? Expanded;

    /// <summary>True for the row that is this installation's stored choice
    /// (catalog NextLaunchGroupId). It is not a pending change: the instance
    /// already uses it whenever the live link is switched to this copy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectLabel))]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    [NotifyPropertyChangedFor(nameof(SelectTip))]
    private bool _isChosen;

    /// <summary>Selection is a radio group: every row can be chosen, including
    /// the live one — choosing it cancels a pending choice instead of writing.</summary>
    public string SelectLabel => IsPending
        ? "已选择"
        : IsChosen
            ? HasPendingSelection ? "取消选择" : IsCurrent ? "当前应用" : "已选择"
            : "选择存档组";

    public bool CanSelect =>
        !IsRunning && !IsPending && !(IsChosen && !HasPendingSelection);

    public string SelectTip => IsRunning
        ? "游戏运行中，选择已锁定"
        : IsPending
            ? "已选择；点上方「应用当前存档组」才会立即切换链接"
            : IsChosen
                ? HasPendingSelection
                    ? "取消待应用的选择，回到这个版本选定的存档组；不写任何文件"
                    : IsCurrent
                        ? "这是当前正在使用的存档组"
                        : "这是该版本选定的存档组；切换到这个版本时会使用它"
                : "只做选择；点上方「应用当前存档组」才会立即切换链接";


    public bool CanEdit => !IsRunning;

    public bool CanExport => !IsRunning;

    public bool CanDelete => !IsRunning && !IsCurrent && !IsNextLaunch && _canDeleteGroup;

    public string DeleteHint =>
        IsCurrent ? "当前存档组，不能删除"
        : IsNextLaunch ? "下次启动组，不能删除"
        : IsRunning ? "游戏运行中，不能删除"
        : !_canDeleteGroup ? "至少保留一个存档组"
        : "移入 Windows 回收站";

    public IRelayCommand ToggleExpandedCommand { get; }

    public IRelayCommand SelectCommand { get; }

    partial void OnIsExpandedChanged(bool value) => Expanded?.Invoke(this, EventArgs.Empty);

    partial void OnHasPendingSelectionChanged(bool value) =>
        SelectCommand.NotifyCanExecuteChanged();

    partial void OnIsPendingChanged(bool value) =>
        SelectCommand.NotifyCanExecuteChanged();

    public IAsyncRelayCommand OpenCommand { get; }

    public IAsyncRelayCommand ExportCommand { get; }

    public IAsyncRelayCommand RenameCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(SelectTip));
        OnPropertyChanged(nameof(DeleteHint));
        ExportCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        SelectCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One collapsible installation block on the saves page.</summary>
public partial class SaveInstallationItem : ObservableObject
{
    private readonly Action<SaveInstallationItem> _expandedChanged;
    private readonly Action<SaveInstallationItem> _selectionChanged;

    public SaveInstallationItem(
        GameInstallation installation,
        bool isActive,
        bool isRunning,
        bool isExpanded,
        bool needsUnmanagedChoice,
        int unmanagedFileCount,
        string noticeMessage,
        SaveGroupRow? currentGroup,
        SaveGroupRow? chosenGroup,
        IReadOnlyList<SaveGroupRow> groups,
        SaveGroupRow? selectedGroup,
        string selectedGroupLabel,
        string linkOwnerLabel,
        Func<SaveInstallationItem, Task> createEmpty,
        Func<SaveInstallationItem, Task> clone,
        Func<SaveInstallationItem, Task> import,
        Func<SaveInstallationItem, Task> applySelection,
        Action<SaveInstallationItem> expandedChanged,
        Action<SaveInstallationItem> selectionChanged)
    {
        Installation = installation;
        IsActive = isActive;
        IsRunning = isRunning;
        NeedsUnmanagedChoice = needsUnmanagedChoice;
        UnmanagedFileCount = unmanagedFileCount;
        NoticeMessage = noticeMessage;
        CurrentGroup = currentGroup;
        ChosenGroup = chosenGroup;
        SelectedGroupLabel = selectedGroupLabel;
        LinkOwnerLabel = linkOwnerLabel;
        _expandedChanged = expandedChanged;
        _selectionChanged = selectionChanged;

        foreach (var group in groups)
        {
            var row = group;
            row.Selected += (_, _) => SelectedGroup = row;
            row.Expanded += (_, _) =>
            {
                // Accordion: opening one group closes the others.
                if (!row.IsExpanded) return;
                foreach (var other in Groups)
                {
                    if (!ReferenceEquals(other, row) && other.IsExpanded) other.IsExpanded = false;
                }
            };
            Groups.Add(row);
        }

        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        CreateEmptyCommand = new AsyncRelayCommand(() => createEmpty(this), () => CanEdit);
        CloneCommand = new AsyncRelayCommand(() => clone(this), () => CanEdit && HasGroups);
        ImportCommand = new AsyncRelayCommand(() => import(this), () => CanImport);
        ApplyCommand = new AsyncRelayCommand(() => applySelection(this), () => CanApply);

        _isExpanded = isExpanded;
        _selectedGroup = selectedGroup;
        UpdatePendingFlags();
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

    public SaveGroupRow? CurrentGroup { get; }

    /// <summary>The group this copy will use next (catalog NextLaunchGroupId).
    /// This is the copy's own “current group”; the live link is a global fact
    /// that only says which copy is switched on right now.</summary>
    public SaveGroupRow? ChosenGroup { get; }

    /// <summary>The group this copy will use next (catalog selection).</summary>
    public string SelectedGroupLabel { get; }

    /// <summary>Which other copy currently owns the live link, empty when this
    /// one does (or when the link points at nothing).</summary>
    public string LinkOwnerLabel { get; }

    public bool HasLinkOwner => LinkOwnerLabel.Length > 0;

    public string CurrentGroupName => CurrentGroup?.Name ?? "未占用";

    public bool HasCurrentGroup => CurrentGroup is not null;

    public string CurrentGroupPath => CurrentGroup?.DirectoryPath ?? "—";

    public string CurrentGroupSummary => CurrentGroup?.SummaryLabel ?? "应用一个组后，从 Steam 启动也会使用它";

    public string ChosenGroupName => ChosenGroup?.Name ?? "未选择";

    public string ChosenGroupPath => ChosenGroup?.DirectoryPath ?? "—";

    public string ChosenGroupSummary => ChosenGroup?.SummaryLabel
        ?? (HasGroups ? "还没有选定存档组" : "还没有存档组；点「新建空组」开始。");

    /// <summary>Small caption under the chosen group: it either says the link
    /// serves this copy, or stays silent and lets the owner line speak.</summary>
    public string LinkUsageLabel => ChosenGroup is null
        ? "还没有选定存档组"
        : CurrentGroup?.Id == ChosenGroup.Id
            ? "链接：正在使用"
            : HasLinkOwner ? string.Empty : "链接当前未指向任何存档组";

    public bool HasLinkUsage => LinkUsageLabel.Length > 0;

    public string GroupCountLabel => $"{Groups.Count} 个存档组";

    public bool HasGroups => Groups.Count > 0;

    public bool HasNoGroups => Groups.Count == 0;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private SaveGroupRow? _selectedGroup;

    [ObservableProperty]
    private bool _isRunning;


    public bool CanEdit => !IsRunning && !NeedsUnmanagedChoice;

    /// <summary>Text for the page-level running banner (see SavesViewModel.AnyRunning).</summary>
    public string RunningNotice =>
        "游戏正在运行；选择、应用、克隆、导入、导出和删除已锁定，请关闭游戏后再操作。";

    public bool CanImport => !IsRunning && !NeedsUnmanagedChoice;

    public bool CanApply =>
        CanEdit
        && SelectedGroup is not null
        && SelectedGroup.Id != ChosenGroup?.Id;

    public string SelectionHint
    {
        get
        {
            if (SelectedGroup is null)
                return HasGroups
                    ? "点「选择存档组」进行选择；点行内其它位置只展开/收起。"
                    : "还没有存档组；点「新建空组」开始。";

            if (SelectedGroup.Id == ChosenGroup?.Id)
                return CurrentGroup?.Id == SelectedGroup.Id
                    ? $"「{SelectedGroup.Name}」已应用；从 Steam 启动也会使用该组。"
                    : HasLinkOwner
                        ? $"「{SelectedGroup.Name}」是该版本选定的存档组；链接现归「{LinkOwnerLabel}」。"
                        : $"「{SelectedGroup.Name}」是该版本选定的存档组；链接当前未指向任何组。";

            return $"待应用：{SelectedGroup.Name}";
        }
    }

    public IRelayCommand ToggleExpandedCommand { get; }

    public IAsyncRelayCommand CreateEmptyCommand { get; }

    public IAsyncRelayCommand CloneCommand { get; }

    public IAsyncRelayCommand ImportCommand { get; }

    public IAsyncRelayCommand ApplyCommand { get; }

    partial void OnIsExpandedChanged(bool value) => _expandedChanged(this);

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanApply));
        CreateEmptyCommand.NotifyCanExecuteChanged();
        CloneCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedGroupChanged(SaveGroupRow? value)
    {
        UpdatePendingFlags();
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(SelectionHint));
        ApplyCommand.NotifyCanExecuteChanged();
        _selectionChanged(this);
    }

    /// <summary>“待应用” belongs to exactly one row: a selection that differs
    /// from this copy's stored choice. Choosing the stored choice again cancels
    /// it; every row learns whether a pending choice exists so the chosen row
    /// can offer 「取消选择」 instead of a dead disabled button.</summary>
    private void UpdatePendingFlags()
    {
        var chosenId = ChosenGroup?.Id;
        var anyPending = SelectedGroup is not null && SelectedGroup.Id != chosenId;
        foreach (var row in Groups)
        {
            row.HasPendingSelection = anyPending;
            row.IsChosen = row.Id == chosenId;
            row.IsPending = ReferenceEquals(row, SelectedGroup) && row.Id != chosenId;
            row.IsSelected = ReferenceEquals(row, SelectedGroup);
        }
    }
}

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

    // UI state is keyed by installation so a monitor refresh never loses the
    // user's pending selection or the expanded block.
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _pending = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    /// <summary>Global fact shown once at the top: the LocalLow junction points
    /// at exactly one group, and this says whose it is.</summary>
    [ObservableProperty]
    private string _liveLinkLabel = string.Empty;

    /// <summary>Normalized game directory of the version selected in the top
    /// instance picker. The view uses this to scroll the matching card into
    /// view when the active version changes.</summary>
    [ObservableProperty]
    private string? _activeInstallationKey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    /// <summary>True while any installation runs the game. The game writes the
    /// one global LocalLow directory, so this locks every save-write control on
    /// the page, not only the running installation.</summary>
    [ObservableProperty]
    private bool _anyRunning;

    public string RunningNotice =>
        "游戏正在运行；选择、应用、克隆、导入、导出和删除已锁定，请关闭游戏后再操作。";

    public SavesViewModel(
        IAppShell shell,
        SaveGroupService saveGroups,
        SaveGroupArchiveService archive,
        SaveSlotReader slotReader,
        IFilePickerService filePicker,
        IUserDialogService dialogs,
        IAppLogger logger,
        GameProcessMonitor processMonitor)
    {
        _shell = shell;
        _saveGroups = saveGroups;
        _archive = archive;
        _slotReader = slotReader;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _logger = logger;
        _processMonitor = processMonitor;
        _shell.Game.Changed += (_, _) =>
        {
            // A pending choice is not carried across instances: the page always
            // reopens on the live group of the instance that is now active.
            ResetTransientState();
            _ = RefreshAsync();
        };

        // A process-state change only flips IsRunning: rebuilding the list here
        // would drop scroll position, expansion and the pending selection.
        _processMonitor.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyRunningState);
    }

    public ObservableCollection<SaveInstallationItem> Instances { get; } = [];

    public Task OnActivatedAsync() => RefreshAsync();

    /// <summary>Pending selections and expanded blocks are page-local UI state:
    /// leaving the page drops both, so the page always reopens on the live group.
    /// Nothing here writes to disk or to the catalog.</summary>
    public void ResetTransientState()
    {
        foreach (var item in Instances)
        {
            foreach (var row in item.Groups)
            {
                if (row.IsExpanded) row.IsExpanded = false;
            }

            item.SelectedGroup = item.CurrentGroup ?? item.Groups.FirstOrDefault();
        }

        // Selection/expansion callbacks repopulate the maps; clear them last so
        // the next refresh starts from the live state rather than stale intent.
        _pending.Clear();
        _expansion.Clear();
    }


    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RefreshCoreAsync();

    private bool CanRefresh() => !IsBusy;

    /// <summary>One save operation at a time: the page disables its controls
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
            var liveOwnerLabel = TryDescribeLiveOwner();
            var items = new List<SaveInstallationItem>();
            foreach (var installation in _shell.Game.Installations)
            {
                try
                {
                    items.Add(await BuildInstallationItemAsync(installation, activePath, liveOwnerLabel));
                }
                catch (Exception exception)
                {
                    _logger.Warn($"读取存档组失败：{exception.Message}");
                }
            }

            Instances.Clear();
            foreach (var item in items) Instances.Add(item);

            if (!SamePathOrNull(ActiveInstallationKey, activePath))
                ActiveInstallationKey = activePath;

            LiveLinkLabel = liveOwnerLabel is null
                ? "LocalLow 链接：未指向任何存档组（首次启动会自动创建）"
                : $"LocalLow 链接 → {liveOwnerLabel}";

            EmptyMessage = Instances.Count == 0
                ? "还没有添加游戏版本。先在设置中添加实例。"
                : string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Resolves the global live link into "instance · group" for the
    /// page header; null when it points at nothing (or is stale).</summary>
    private string? TryDescribeLiveOwner()
    {
        try
        {
            var owner = _saveGroups.GetLiveOwner();
            if (owner is null) return null;

            var installation = _shell.Game.Installations.FirstOrDefault(candidate =>
                string.Equals(_saveGroups.GetInstallationKey(candidate), owner.InstallationKey,
                    StringComparison.OrdinalIgnoreCase));

            var name = installation?.DisplayLabel ?? owner.GameDirectory;
            return $"{name} · {owner.Group.Name}";
        }
        catch (Exception exception)
        {
            _logger.Warn($"读取当前存档链接失败：{exception.Message}");
            return null;
        }
    }

    private async Task<SaveInstallationItem> BuildInstallationItemAsync(
        GameInstallation installation,
        string? activeGameDirectory,
        string? liveOwnerLabel)
    {
        // Two copies of the same edition share Profile.Id; only the exact game
        // directory is the active install.
        var isActive = activeGameDirectory is not null
                       && SameDirectory(installation.GameDirectory, activeGameDirectory);
        var isRunning = AnyRunning;
        var key = _saveGroups.GetInstallationKey(installation);

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
        var pendingId = _pending.TryGetValue(key, out var stored) ? stored : (Guid?)null;
        if (pendingId is not null && rows.All(row => row.Id != pendingId))
        {
            _pending.Remove(key);
            pendingId = null;
        }

        var selected = rows.FirstOrDefault(row => row.Id == pendingId)
                       ?? rows.FirstOrDefault(row => row.Id == nextLaunchId)
                       ?? current
                       ?? rows.FirstOrDefault();

        var isExpanded = _expansion.TryGetValue(key, out var expanded) ? expanded : isActive;

        // The group this copy will use next (catalog selection), independent of
        // which group currently owns the global LocalLow link.
        var chosen = rows.FirstOrDefault(row => row.Id == nextLaunchId);
        var selectedLabel = chosen?.Name
                            ?? rows.FirstOrDefault()?.Name
                            ?? "未选择";
        var linkOwner = current is null && liveOwnerLabel is not null ? liveOwnerLabel : string.Empty;

        return new SaveInstallationItem(
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
            linkOwner,
            CreateEmptyAsync,
            CloneAsync,
            ImportAsync,
            ApplySelectionAsync,
            RememberExpansion,
            RememberSelection);
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
    /// and the pending selection are left untouched.</summary>
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
        var key = _saveGroups.GetInstallationKey(item.Installation);

        if (!item.IsExpanded)
        {
            _expansion.Remove(key);
            return;
        }

        // Installation cards behave like an accordion: only one block stays
        // open. Collapsing the siblings first also keeps the persisted state
        // consistent when the page is refreshed.
        foreach (var other in Instances)
        {
            if (!ReferenceEquals(other, item) && other.IsExpanded)
                other.IsExpanded = false;
        }

        _expansion.Clear();
        _expansion[key] = true;
    }

    private void RememberSelection(SaveInstallationItem item)
    {
        var key = _saveGroups.GetInstallationKey(item.Installation);
        if (item.SelectedGroup is null || item.SelectedGroup.Id == item.CurrentGroup?.Id)
            _pending.Remove(key);
        else
            _pending[key] = item.SelectedGroup.Id;
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
                SaveGameInfo.FromInstallation(row.Installation, DetectGameVersion(row.Installation)),
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
            if (result.Success)
                _pending.Remove(_saveGroups.GetInstallationKey(row.Installation));
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            if (result.Success) await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplySelectionAsync(SaveInstallationItem item)
    {
        if (item.IsRunning)
        {
            _shell.SetStatus("游戏正在运行，先关闭游戏再切换存档组。", StatusKind.Warning);
            return;
        }

        if (item.SelectedGroup is not { } group)
        {
            _shell.SetStatus("请先点击一个存档组。", StatusKind.Info);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _saveGroups.ApplyCurrentAsync(item.Installation, group.Id);
            if (result.Success)
                _pending.Remove(_saveGroups.GetInstallationKey(item.Installation));
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            if (result.Success) await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
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
            "创建空的 LocalLow 存档目录；不会复制当前组，也不会更改 Mod 组。",
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
        var targetVersion = DetectGameVersion(item.Installation);
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

        await RefreshAsync();
        if (group is null) return;

        _shell.SetStatus(
            applied
                ? $"「{group.Name}」已创建，并作为当前存档。"
                : $"「{group.Name}」已创建；当前存档保持不变。",
            applied ? StatusKind.Success : StatusKind.Info);
    }

    private static string? DetectGameVersion(GameInstallation installation)
    {
        try
        {
            var path = installation.ExecutablePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch
        {
            return null;
        }
    }
}
