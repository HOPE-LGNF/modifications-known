using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.ViewModels;

/// <summary>One snapshot of an installation card, built from a fresh read and
/// applied to the existing card so refresh does not recreate the list.</summary>
internal sealed record SaveInstallationSnapshot(
    GameInstallation Installation,
    bool IsActive,
    bool IsRunning,
    bool IsExpanded,
    bool NeedsUnmanagedChoice,
    int UnmanagedFileCount,
    string NoticeMessage,
    SaveGroupRow? CurrentGroup,
    SaveGroupRow? ChosenGroup,
    IReadOnlyList<SaveGroupRow> Groups,
    SaveGroupRow? SelectedGroup,
    string SelectedGroupLabel,
    string LinkOwnerLabel);

/// <summary>One collapsible installation block on the saves page.</summary>
public partial class SaveInstallationItem : ObservableObject
{
    private readonly Action<SaveInstallationItem> _expandedChanged;
    private readonly Action<SaveInstallationItem> _selectionChanged;
    private bool _suppressSelectionNotification;
    private bool _suppressExpansionNotification;

    internal SaveInstallationItem(
        SaveInstallationSnapshot snapshot,
        Func<SaveInstallationItem, Task> createEmpty,
        Func<SaveInstallationItem, Task> clone,
        Func<SaveInstallationItem, Task> import,
        Action<SaveInstallationItem> expandedChanged,
        Action<SaveInstallationItem> selectionChanged)
    {
        Installation = snapshot.Installation;
        _expandedChanged = expandedChanged;
        _selectionChanged = selectionChanged;

        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        CreateEmptyCommand = new AsyncRelayCommand(() => createEmpty(this), () => CanEdit);
        CloneCommand = new AsyncRelayCommand(() => clone(this), () => CanEdit && HasGroups);
        ImportCommand = new AsyncRelayCommand(() => import(this), () => CanImport);

        Apply(snapshot);
    }

    public GameInstallation Installation { get; private set; }

    public string Name => Installation.DisplayLabel;

    public string GamePath => Installation.GameDirectory;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>True when the global LocalLow link currently points at a group
    /// of this installation. Independent of the top version selector.</summary>
    public bool IsLiveOwner => CurrentGroup is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private bool _needsUnmanagedChoice;

    [ObservableProperty]
    private int _unmanagedFileCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeMessage = string.Empty;

    public bool HasNotice => NoticeMessage.Length > 0;

    public ObservableCollection<SaveGroupRow> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveOwner))]
    [NotifyPropertyChangedFor(nameof(CurrentGroupName))]
    [NotifyPropertyChangedFor(nameof(HasCurrentGroup))]
    [NotifyPropertyChangedFor(nameof(CurrentGroupPath))]
    [NotifyPropertyChangedFor(nameof(CurrentGroupSummary))]
    [NotifyPropertyChangedFor(nameof(LinkUsageLabel))]
    [NotifyPropertyChangedFor(nameof(HasLinkUsage))]
    private SaveGroupRow? _currentGroup;

    /// <summary>The group this copy will use next (catalog NextLaunchGroupId).
    /// This is the copy's own “current group”; the live link is a global fact
    /// that only says which copy is switched on right now.</summary>
    public SaveGroupRow? ChosenGroup { get; private set; }

    /// <summary>The group this copy will use next (catalog selection).</summary>
    public string SelectedGroupLabel { get; private set; } = string.Empty;

    /// <summary>Which other copy currently owns the live link, empty when this
    /// one does (or when the link points at nothing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkOwner))]
    [NotifyPropertyChangedFor(nameof(LinkUsageLabel))]
    [NotifyPropertyChangedFor(nameof(HasLinkUsage))]
    private string _linkOwnerLabel = string.Empty;

    public bool HasLinkOwner => LinkOwnerLabel.Length > 0;

    public string CurrentGroupName => CurrentGroup?.Name ?? "未占用";

    public bool HasCurrentGroup => CurrentGroup is not null;

    public string CurrentGroupPath => CurrentGroup?.DirectoryPath ?? "—";

    public string CurrentGroupSummary => CurrentGroup?.SummaryLabel ?? "当前没有正在使用的存档组";

    public string ChosenGroupName => ChosenGroup?.Name ?? "未选择";

    public string ChosenGroupPath => ChosenGroup?.DirectoryPath ?? "—";

    public string ChosenGroupSummary => ChosenGroup?.SummaryLabel
        ?? (HasGroups ? "还没有选定存档组" : "还没有存档组；点「新建空组」开始。");

    public string ChosenSlotCountLabel => ChosenGroup?.SlotCountLabel ?? "—";

    public string ChosenAutosaveCountLabel => ChosenGroup?.AutosaveCountLabel ?? "—";

    public string ChosenRecentSaveLabel => ChosenGroup?.RecentSaveLabel ?? "尚无存档文件";

    /// <summary>Small caption under the chosen group: it either says the link
    /// serves this copy, or stays silent and lets the owner line speak.</summary>
    public string LinkUsageLabel => ChosenGroup is null
        ? "还没有选定存档组"
        : CurrentGroup?.Id == ChosenGroup.Id
            ? "正在使用"
            : HasLinkOwner ? string.Empty : "当前没有正在使用的存档组";

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

    public bool CanImport => !IsRunning && !NeedsUnmanagedChoice;

    /// <summary>True while the row selection differs from this copy's stored
    /// choice, i.e. while an immediate save is in flight.</summary>
    public bool HasPending =>
        SelectedGroup is not null
        && SelectedGroup.Id != ChosenGroup?.Id;

    public IRelayCommand ToggleExpandedCommand { get; }

    public IAsyncRelayCommand CreateEmptyCommand { get; }

    public IAsyncRelayCommand CloneCommand { get; }

    public IAsyncRelayCommand ImportCommand { get; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!_suppressExpansionNotification)
            _expandedChanged(this);
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanImport));
        CreateEmptyCommand.NotifyCanExecuteChanged();
        CloneCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedGroupChanged(SaveGroupRow? value)
    {
        UpdatePendingFlags();
        OnPropertyChanged(nameof(HasPending));
        if (!_suppressSelectionNotification)
            _selectionChanged(this);
    }

    partial void OnNeedsUnmanagedChoiceChanged(bool value)
    {
        CreateEmptyCommand.NotifyCanExecuteChanged();
        CloneCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Applies a freshly read snapshot to this card. The card object stays in
    /// the outer Instances list, so scroll, expansion and focus survive a page
    /// refresh. Group rows are recreated because their save details are
    /// immutable, but their expanded IDs are carried over.
    /// </summary>
    internal void Apply(SaveInstallationSnapshot snapshot)
    {
        var expandedIds = Groups
            .Where(row => row.IsExpanded)
            .Select(row => row.Id)
            .ToHashSet();

        if (!ReferenceEquals(Installation, snapshot.Installation))
        {
            Installation = snapshot.Installation;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(GamePath));
        }

        IsActive = snapshot.IsActive;
        IsRunning = snapshot.IsRunning;
        NeedsUnmanagedChoice = snapshot.NeedsUnmanagedChoice;
        UnmanagedFileCount = snapshot.UnmanagedFileCount;
        NoticeMessage = snapshot.NoticeMessage;
        CurrentGroup = snapshot.CurrentGroup;
        ChosenGroup = snapshot.ChosenGroup;
        SelectedGroupLabel = snapshot.SelectedGroupLabel;
        LinkOwnerLabel = snapshot.LinkOwnerLabel;

        _suppressSelectionNotification = true;
        try
        {
            Groups.Clear();
            foreach (var row in snapshot.Groups)
            {
                if (expandedIds.Contains(row.Id)) row.IsExpanded = true;
                AttachGroupRow(row);
                Groups.Add(row);
            }

            SelectedGroup = snapshot.SelectedGroup;

            _suppressExpansionNotification = true;
            try
            {
                IsExpanded = snapshot.IsExpanded;
            }
            finally
            {
                _suppressExpansionNotification = false;
            }
        }
        finally
        {
            _suppressSelectionNotification = false;
        }

        UpdatePendingFlags();
        NotifyChosenGroupChanged();
        NotifyGroupsChanged();
    }

    private void AttachGroupRow(SaveGroupRow row)
    {
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
    }

    private void NotifyGroupsChanged()
    {
        OnPropertyChanged(nameof(GroupCountLabel));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasNoGroups));
        OnPropertyChanged(nameof(ChosenGroupSummary));
        OnPropertyChanged(nameof(HasPending));
        CloneCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Updates the transient row labels while a selection is being
    /// saved. Selection is immediate; the stored choice is the source of truth
    /// once the write finishes.</summary>
    private void UpdatePendingFlags()
    {
        var chosenId = ChosenGroup?.Id;
        foreach (var row in Groups)
        {
            row.IsChosen = row.Id == chosenId;
            row.IsPending = ReferenceEquals(row, SelectedGroup) && row.Id != chosenId;
            row.IsSelected = ReferenceEquals(row, SelectedGroup);
        }
    }

    /// <summary>
    /// Applies a successful selection without rebuilding the whole installation
    /// list. Used for non-active copies, where selecting a group only rewrites
    /// that copy's stored NextLaunchGroupId; the live LocalLow link and the rest
    /// of the page stay untouched.
    /// </summary>
    public void AdoptChosenGroup(SaveGroupRow group)
    {
        if (!Groups.Contains(group)) return;

        foreach (var row in Groups)
            row.IsNextLaunch = ReferenceEquals(row, group);

        ChosenGroup = group;
        SelectedGroupLabel = group.Name;

        // Update the visual selection after ChosenGroup so the generated
        // callback sees no pending write and does not schedule another save.
        SelectedGroup = group;
        UpdatePendingFlags();
        NotifyChosenGroupChanged();
    }

    private void NotifyChosenGroupChanged()
    {
        OnPropertyChanged(nameof(ChosenGroup));
        OnPropertyChanged(nameof(SelectedGroupLabel));
        OnPropertyChanged(nameof(ChosenGroupName));
        OnPropertyChanged(nameof(ChosenGroupPath));
        OnPropertyChanged(nameof(ChosenGroupSummary));
        OnPropertyChanged(nameof(ChosenSlotCountLabel));
        OnPropertyChanged(nameof(ChosenAutosaveCountLabel));
        OnPropertyChanged(nameof(ChosenRecentSaveLabel));
        OnPropertyChanged(nameof(LinkUsageLabel));
        OnPropertyChanged(nameof(HasLinkUsage));
        OnPropertyChanged(nameof(HasPending));
    }
}
