using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.App.ViewModels;

public partial class HomeViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly IGameLocator _locator;
    private readonly IModManager _modManager;
    private readonly IGameLauncher _launcher;
    private readonly IAppSettingsService _settings;
    private readonly INexusModsClient _nexus;
    private DateTimeOffset _lastAccountCheck;

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
    private string _nexusAccountLabel = "账号类型未知";

    [ObservableProperty]
    private string _nexusAccountDetail = "配置 API Key 后可检查";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _nexusConfigured;

    public bool NexusNotConfigured => !NexusConfigured;

    partial void OnNexusConfiguredChanged(bool value) =>
        OnPropertyChanged(nameof(NexusNotConfigured));

    public HomeViewModel(
        IAppShell shell,
        IGameLocator locator,
        IModManager modManager,
        IGameLauncher launcher,
        IAppSettingsService settings,
        INexusModsClient nexus)
    {
        _shell = shell;
        _locator = locator;
        _modManager = modManager;
        _launcher = launcher;
        _settings = settings;
        _nexus = nexus;
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
                NexusConfigured = !string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey);
                NexusLabel = NexusConfigured ? "API Key 已配置" : "未配置 API Key";
                await RefreshNexusAccountAsync();
                return;
            }

            HasGame = true;
            GameTitle = installation.DisplayLabel;
            GameSubtitle = $"{installation.Profile.EditionLabel} · {installation.Profile.SteamAppId}";
            EditionLabel = installation.Profile.EditionLabel;
            GamePath = installation.GameDirectory;
            BepInExLabel = installation.HasBepInEx ? "BepInEx 已就绪" : "BepInEx 未安装";
            BepInExReady = installation.HasBepInEx;
            NexusConfigured = !string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey);
            NexusLabel = NexusConfigured ? "API Key 已配置" : "未配置 API Key";
            await RefreshNexusAccountAsync();

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

    private async Task RefreshNexusAccountAsync()
    {
        if (!NexusConfigured)
        {
            NexusAccountLabel = "未配置";
            NexusAccountDetail = "配置 API Key 后可显示普通 / Premium";
            return;
        }

        // The home page is re-activated often; a five-minute cache avoids
        // hammering /users/validate on every navigation.
        if (DateTimeOffset.UtcNow - _lastAccountCheck < TimeSpan.FromMinutes(5)) return;

        try
        {
            var status = await _nexus.GetAccountStatusAsync();
            NexusAccountLabel = status.TierLabel;
            NexusAccountDetail = status.IsPremium
                ? "Premium · 可直接生成下载链接"
                : $"普通账号 · {status.QuotaLabel}";
        }
        catch (Exception exception)
        {
            NexusAccountLabel = "未知";
            NexusAccountDetail = $"账号查询失败：{exception.Message}";
        }
        finally
        {
            _lastAccountCheck = DateTimeOffset.UtcNow;
        }
    }

    [RelayCommand]
    private Task LaunchAsync() => _shell.LaunchGameAsync();

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
