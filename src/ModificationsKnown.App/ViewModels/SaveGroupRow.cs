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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(DeleteHint))]
    private bool _isNextLaunch;

    public bool IsDefault => Group.IsDefault;

    public bool HasCurrentTag => IsCurrent;

    public string CurrentTag => IsCurrent ? "当前" : string.Empty;

    public string DirectoryPath => Group.DirectoryPath;

    public SaveGroupDetails Details { get; }

    public IReadOnlyList<SaveSlotCard> Saves { get; }

    public bool HasSaves => Saves.Count > 0;

    public bool IsEmpty => Saves.Count == 0;

    public string SummaryLabel => Details.SummaryLabel;

    public string SlotCountLabel => Details.Layout == SaveLayoutKind.Empty
        ? "—"
        : $"{Details.SlotCount} 个槽位";

    public string AutosaveCountLabel => Details.Layout == SaveLayoutKind.Empty
        ? "—"
        : $"{Details.Saves.Count(save => save.Kind == SaveArtifactKind.Autosave)} 个自动存档";

    public string RecentSaveLabel => Details.MostRecentDetail ?? "尚无存档文件";



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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectLabel))]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    [NotifyPropertyChangedFor(nameof(SelectTip))]
    [NotifyCanExecuteChangedFor(nameof(SelectCommand))]
    private bool _isChosen;

    /// <summary>True for the row chosen with “选择存档组”; drives the accent border.</summary>
    [ObservableProperty]
    private bool _isSelected;


    /// <summary>Raised by the row's “选择存档组” button; the page saves it
    /// immediately.</summary>
    public event EventHandler? Selected;

    /// <summary>Raised when <see cref="IsExpanded"/> changes; the page keeps one row open.</summary>
    public event EventHandler? Expanded;

    /// <summary>Selection is immediate: every row except the stored choice can
    /// be clicked; the page writes the choice as soon as it changes.</summary>
    public string SelectLabel => IsPending
        ? "已选择"
        : IsChosen
            ? IsCurrent ? "当前使用" : "已选择"
            : "选择存档组";

    public bool CanSelect =>
        !IsRunning && !IsPending && !IsChosen;

    public string SelectTip => IsRunning
        ? "游戏运行中，选择已锁定"
        : IsPending
            ? "已选择，正在保存"
            : IsChosen
                ? IsCurrent
                    ? "这是当前正在使用的存档组"
                    : "这是该实例的选定组；切到该实例时会使用它"
                : "点击后立即保存；当前实例会切换 LocalLow，其他实例只更新选定组";


    public bool CanEdit => !IsRunning;

    public bool CanExport => !IsRunning;

    public bool CanDelete => !IsRunning && !IsCurrent && !IsNextLaunch && _canDeleteGroup;

    public string DeleteHint =>
        IsCurrent ? "当前正在使用，不能删除"
        : IsNextLaunch ? "该实例的选定组，不能删除"
        : IsRunning ? "游戏运行中，不能删除"
        : !_canDeleteGroup ? "至少保留一个存档组"
        : "移入 Windows 回收站";

    public IRelayCommand ToggleExpandedCommand { get; }

    public string Chevron => IsExpanded ? "▾" : "◂";

    public IRelayCommand SelectCommand { get; }

    partial void OnIsExpandedChanged(bool value)
    {
        Expanded?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(Chevron));
    }

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
