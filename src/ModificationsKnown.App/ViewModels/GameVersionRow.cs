using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.App.ViewModels;

public partial class GameVersionRow : ObservableObject
{
    [ObservableProperty]
    private string _alias;

    [ObservableProperty]
    private bool _isCurrent;

    public GameVersionRow(
        GameInstallation installation,
        bool isCurrent,
        Func<GameVersionRow, Task> saveAlias,
        Func<GameVersionRow, Task> select,
        Func<GameVersionRow, Task> installLoader)
    {
        Installation = installation;
        _alias = string.IsNullOrWhiteSpace(installation.Alias)
            ? installation.Profile.EditionLabel
            : installation.Alias;
        _isCurrent = isCurrent;
        SaveAliasCommand = new AsyncRelayCommand(() => saveAlias(this));
        SelectCommand = new AsyncRelayCommand(() => select(this));
        InstallLoaderCommand = new AsyncRelayCommand(() => installLoader(this));
    }

    public GameInstallation Installation { get; }

    public string EditionLabel => Installation.Profile.EditionLabel;

    public string Path => Installation.GameDirectory;

    public string LoaderLabel => Installation.HasBepInEx ? "BepInEx 已安装" : "BepInEx 未安装";

    public bool CanInstallLoader => !Installation.HasBepInEx;

    public IAsyncRelayCommand SaveAliasCommand { get; }

    public IAsyncRelayCommand SelectCommand { get; }

    public IAsyncRelayCommand InstallLoaderCommand { get; }
}
