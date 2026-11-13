using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.App.ViewModels;

public partial class GameVersionRow : ObservableObject
{
    private readonly string _originalAlias;

    [ObservableProperty]
    private string _alias;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isSavingAlias;

    public GameVersionRow(
        GameInstallation installation,
        bool isCurrent,
        Func<GameVersionRow, Task> saveAlias,
        Func<GameVersionRow, Task> installLoader,
        Func<GameVersionRow, Task> openLoaderDownload,
        Func<GameVersionRow, Task> delete)
    {
        Installation = installation;
        _originalAlias = string.IsNullOrWhiteSpace(installation.Alias)
            ? installation.Profile.EditionLabel
            : installation.Alias;
        _alias = _originalAlias;
        _isCurrent = isCurrent;

        SaveAliasCommand = new AsyncRelayCommand(
            async () =>
            {
                IsSavingAlias = true;
                try
                {
                    await saveAlias(this);
                }
                finally
                {
                    IsSavingAlias = false;
                }
            },
            () => CanSaveAlias);

        InstallLoaderCommand = new AsyncRelayCommand(() => installLoader(this));
        OpenLoaderDownloadCommand = new AsyncRelayCommand(() => openLoaderDownload(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }

    public GameInstallation Installation { get; }

    public string EditionLabel => Installation.Profile.EditionLabel;

    public string Path => Installation.GameDirectory;

    public string LoaderLabel => Installation.HasBepInEx ? "BepInEx 已安装" : "BepInEx 未安装";

    public bool CanInstallLoader => !Installation.HasBepInEx;

    /// <summary>Show the save button only when the trimmed text really differs
    /// from the stored alias; an empty box is dirty but not savable.</summary>
    public bool IsAliasDirty =>
        !string.Equals(Alias.Trim(), _originalAlias.Trim(), StringComparison.Ordinal);

    public bool CanSaveAlias => IsAliasDirty && !IsSavingAlias && Alias.Trim().Length > 0;

    public string SaveAliasText => IsSavingAlias ? "保存中…" : "保存名称";

    partial void OnAliasChanged(string value)
    {
        OnPropertyChanged(nameof(IsAliasDirty));
        OnPropertyChanged(nameof(CanSaveAlias));
        SaveAliasCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSavingAliasChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSaveAlias));
        OnPropertyChanged(nameof(SaveAliasText));
        SaveAliasCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Escape restores the stored alias without saving.</summary>
    public void ResetAlias() => Alias = _originalAlias;

    public IAsyncRelayCommand SaveAliasCommand { get; }

    public IAsyncRelayCommand InstallLoaderCommand { get; }

    public IAsyncRelayCommand OpenLoaderDownloadCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }
}
