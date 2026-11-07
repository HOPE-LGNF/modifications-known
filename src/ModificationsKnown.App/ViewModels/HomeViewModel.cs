using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.App.ViewModels;

public partial class HomeViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly IGameLocator _locator;
    private readonly IModManager _modManager;
    private readonly IGameLauncher _launcher;
    private readonly IAppSettingsService _settings;

    [ObservableProperty]
    private bool _hasGame;

    [ObservableProperty]
    private string _gameTitle = "尚未检测到游戏";

    [ObservableProperty]
    private string _gameSubtitle = "安装游戏后，启动器会自动找到正式版或试玩版。";

    [ObservableProperty]
    private string _editionLabel = "未知版本";

    [ObservableProperty]
    private string _gamePath = "—";

    [ObservableProperty]
    private string _bepInExLabel = "未检测 BepInEx";

    [ObservableProperty]
    private bool _bepInExReady;

    [ObservableProperty]
    private string _modCountLabel = "0";

    [ObservableProperty]
    private string _enabledCountLabel = "0";

    [ObservableProperty]
    private string _nexusLabel = "未配置 API Key";

    [ObservableProperty]
    private bool _isBusy;

    public HomeViewModel(
        IAppShell shell,
        IGameLocator locator,
        IModManager modManager,
        IGameLauncher launcher,
        IAppSettingsService settings)
    {
        _shell = shell;
        _locator = locator;
        _modManager = modManager;
        _launcher = launcher;
        _settings = settings;
        _shell.Game.Changed += OnGameChanged;
    }

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            if (_shell.Game.Selected is not { } installation)
            {
                HasGame = false;
                GameTitle = "尚未检测到游戏";
                GameSubtitle = "请在设置中手动选择游戏目录，或安装 Steam 版《未知伤亡》。";
                EditionLabel = "未知版本";
                GamePath = "—";
                BepInExLabel = "未检测 BepInEx";
                BepInExReady = false;
                ModCountLabel = "0";
                EnabledCountLabel = "0";
                NexusLabel = string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey)
                    ? "未配置 API Key"
                    : "API Key 已配置";
                return;
            }

            HasGame = true;
            GameTitle = installation.Profile.DisplayName;
            GameSubtitle = $"{installation.Profile.EditionLabel} · {installation.Profile.SteamAppId}";
            EditionLabel = installation.Profile.EditionLabel;
            GamePath = installation.GameDirectory;
            BepInExLabel = installation.HasBepInEx ? "BepInEx 已就绪" : "BepInEx 未安装";
            BepInExReady = installation.HasBepInEx;
            NexusLabel = string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey)
                ? "未配置 API Key"
                : "API Key 已配置";

            if (installation.HasPluginsDirectory)
            {
                var mods = await _modManager.ScanAsync(installation);
                ModCountLabel = mods.Count.ToString();
                EnabledCountLabel = mods.Count(mod => mod.IsEnabled).ToString();
            }
            else
            {
                ModCountLabel = "0";
                EnabledCountLabel = "0";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (_shell.Game.Selected is not { } installation)
        {
            _shell.SetStatus("请先检测游戏或手动选择游戏目录。", StatusKind.Warning);
            return;
        }

        var result = string.IsNullOrWhiteSpace(installation.ExecutablePath)
            ? await _launcher.LaunchViaSteamAsync(installation.Profile)
            : await _launcher.LaunchDirectAsync(installation);

        _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (_shell.Game.Selected is not { } installation)
        {
            _shell.SetStatus("尚未选择游戏安装。", StatusKind.Warning);
            return;
        }

        var result = await _launcher.OpenGameFolderAsync(installation);
        _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
    }

    [RelayCommand]
    private async Task RefreshGameAsync()
    {
        await _shell.RefreshGameAsync();
        await RefreshAsync();
    }

    private void OnGameChanged(object? sender, EventArgs e) => _ = RefreshAsync();
}
