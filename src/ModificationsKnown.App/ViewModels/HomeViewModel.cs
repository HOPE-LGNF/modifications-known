using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.App.ViewModels;

public sealed record HomeChecklistItem(
    int Number,
    string Title,
    string Detail,
    bool IsDone,
    string ActionText,
    string TargetPage)
{
    public bool IsPending => !IsDone;

    public string Marker => IsDone ? "✓" : Number.ToString();
}

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
    private string _gamePath = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BepInExShortLabel))]
    private bool _bepInExReady;

    public string BepInExShortLabel => BepInExReady ? "已就绪" : "未安装";

    [ObservableProperty]
    private string _modCountLabel = "0";

    [ObservableProperty]
    private string _enabledCountLabel = "0";

    [ObservableProperty]
    private string _nexusAccountLabel = "账号类型未知";

    [ObservableProperty]
    private string _nexusAccountDetail = "配置 API Key 后可检查";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _nexusConfigured;

    private int _modCount;

    public ObservableCollection<HomeChecklistItem> Checklist { get; } = [];

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
                GamePath = "—";
                BepInExReady = false;
                SetModCounts(0, 0);
                NexusConfigured = !string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey);
                await RefreshNexusAccountAsync();
                return;
            }

            HasGame = true;
            GameTitle = installation.DisplayLabel;
            GameSubtitle = $"{installation.Profile.EditionLabel} · {installation.Profile.SteamAppId}";
            GamePath = installation.GameDirectory;
            BepInExReady = installation.HasBepInEx;
            NexusConfigured = !string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey);
            await RefreshNexusAccountAsync();

            if (installation.HasPluginsDirectory)
            {
                var mods = await _modManager.ScanAsync(installation);
                SetModCounts(mods.Count, mods.Count(mod => mod.IsEnabled));
            }
            else
            {
                SetModCounts(0, 0);
            }
        }
        finally
        {
            RebuildChecklist();
            IsBusy = false;
        }
    }

    private void SetModCounts(int total, int enabled)
    {
        _modCount = total;
        ModCountLabel = total.ToString();
        EnabledCountLabel = enabled.ToString();
    }

    private void RebuildChecklist()
    {
        Checklist.Clear();
        Checklist.Add(new(1, "选择游戏版本",
            HasGame ? GameTitle : "在设置中添加游戏实例",
            HasGame, "去设置", "settings"));
        Checklist.Add(new(2, "安装 BepInEx",
            BepInExReady ? "BepInEx 已就绪" : "在设置的版本列表中，从“⋯”菜单安装",
            BepInExReady, "去设置", "settings"));
        Checklist.Add(new(3, "配置 Nexus API Key",
            NexusConfigured ? NexusAccountDetail : "浏览可匿名，查看文件和下载需要 API Key",
            NexusConfigured, "去设置", "settings"));
        Checklist.Add(new(4, "安装第一个模组",
            _modCount > 0 ? $"已安装 {_modCount} 个包" : "到模组发现浏览，或把压缩包拖进窗口",
            _modCount > 0, "去模组发现", "discover"));
    }

    [RelayCommand]
    private void GoTo(string page) => _shell.NavigateTo(page);

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
