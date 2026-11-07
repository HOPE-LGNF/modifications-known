using System.Reflection;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.App.Theming;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Network;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.ViewModels;

public partial class ShellViewModel : ObservableObject, IAppShell
{
    private readonly IAppLogger _logger;
    private readonly IGameLocator _locator;
    private readonly IGameLauncher _launcher;
    private readonly IUserDialogService _dialogs;
    private readonly Dictionary<string, object> _pages;
    private bool _initialized;

    [ObservableProperty]
    private NavigationItem? _selectedNavigation;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _statusMessage = "准备就绪。";

    [ObservableProperty]
    private StatusKind _statusKind = StatusKind.Info;

    [ObservableProperty]
    private IBrush _statusBrush = new SolidColorBrush(Color.Parse("#79C99E"));

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLaunching;

    public bool CanLaunch => !IsLaunching;

    public string LaunchButtonText => IsLaunching ? "启动中…" : "启动游戏";

    partial void OnIsLaunchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchButtonText));
    }

    public ShellViewModel(
        IAppSettingsService settings,
        IAppLogger logger,
        IGameLocator locator,
        INexusModsClient nexus,
        IModManager modManager,
        IModInstaller installer,
        IFileDownloader downloader,
        IGameLauncher launcher,
        IUserDialogService dialogs,
        IFilePickerService filePicker,
        ThumbnailService thumbnails)
    {
        Settings = settings;
        _logger = logger;
        _locator = locator;
        _launcher = launcher;
        _dialogs = dialogs;

        Game = new GameContext();
        NavigationItems =
        [
            new("home", "概览", "游戏状态与快捷启动", "⌂"),
            new("discover", "模组发现", "从 Nexus Mods 浏览与安装", "⌕"),
            new("installed", "已安装", "启用、停用与清理本地模组", "▣"),
            new("appearance", "启动器外观", "主题变量与基础皮肤", "◈"),
            new("gameskin", "游戏皮肤", "游戏内外观管理", "♧", "开发中", false),
            new("settings", "设置", "路径、API 与日志", "⚙")
        ];

        var nexusMatch = new NexusMatchService(nexus, settings, logger);
        var sources = new JsonInstalledSourceStore(logger);
        var operations = new ModOperationService(modManager, installer);
        _pages = new Dictionary<string, object>
        {
            ["home"] = new HomeViewModel(this, locator, modManager, launcher, settings),
            ["discover"] = new DiscoverViewModel(this, nexus, settings, downloader, operations, thumbnails, filePicker, sources),
            ["installed"] = new InstalledViewModel(this, operations, dialogs, settings, nexusMatch, sources),
            ["appearance"] = new LauncherSkinViewModel(this, settings, filePicker, logger),
            ["gameskin"] = new GameSkinViewModel(),
            ["settings"] = new SettingsViewModel(this, settings, locator, nexus, filePicker, logger)
        };

        SelectedNavigation = NavigationItems[0];
        CurrentPage = _pages["home"];
        Game.Changed += (_, _) => OnPropertyChanged(nameof(CurrentGameLabel));
    }

    public IAppSettingsService Settings { get; }

    public GameContext Game { get; }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public string CurrentGameLabel => Game.Selected is null
        ? "未检测到游戏"
        : $"{Game.Selected.Profile.DisplayName} · {Game.Selected.Profile.EditionLabel}";

    public string VersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "v0.1.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        ThemeService.Apply(Settings.Current.ThemePreset);
        SetStatus("正在检测游戏安装…");
        await RefreshGameAsync();
        await ActivateCurrentPageAsync();
    }

    public void SetStatus(string message, StatusKind kind = StatusKind.Info)
    {
        StatusMessage = message;
        StatusKind = kind;
        StatusBrush = new SolidColorBrush(Color.Parse(kind switch
        {
            StatusKind.Success => ThemeService.Current.Success,
            StatusKind.Warning => ThemeService.Current.Warning,
            StatusKind.Error => ThemeService.Current.Danger,
            _ => ThemeService.Current.Accent
        }));

        if (kind is StatusKind.Error or StatusKind.Warning)
            _logger.Warn(message);
        else
            _logger.Info(message);
    }

    public async Task HandleNxmLinkAsync(string raw)
    {
        if (_pages.GetValueOrDefault("discover") is not DiscoverViewModel discover)
        {
            SetStatus("模组发现页尚未初始化。", StatusKind.Error);
            return;
        }

        NavigateTo("discover");
        await discover.HandleNxmLinkAsync(raw);
    }

    public void NavigateTo(string key)
    {
        var item = NavigationItems.FirstOrDefault(candidate => candidate.Key == key);
        if (item is not null) SelectedNavigation = item;
    }

    public async Task RefreshGameAsync()
    {
        IsBusy = true;
        try
        {
            await Game.RefreshAsync(_locator, Settings.Current.PreferredInstallationId);
            if (Game.Selected is null)
            {
                SetStatus("没有检测到《未知伤亡》。请在设置中手动选择游戏目录。", StatusKind.Warning);
                return;
            }

            Settings.Current.PreferredInstallationId = Game.Selected.Profile.Id;
            await Settings.SaveAsync();
            SetStatus($"已检测到 {Game.Selected.Profile.EditionLabel}：{Game.Selected.GameDirectory}", StatusKind.Success);
        }
        catch (Exception exception)
        {
            _logger.Error("检测游戏失败", exception);
            SetStatus($"检测游戏失败：{exception.Message}", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NavigateAsync(NavigationItem? item)
    {
        if (item is null || !item.IsEnabled) return;
        SelectedNavigation = item;
        await ActivateCurrentPageAsync();
    }

    [RelayCommand]
    public async Task LaunchGameAsync()
    {
        if (IsLaunching) return;

        if (Game.Selected is not { } installation)
        {
            SetStatus("请先检测游戏或手动选择游戏目录。", StatusKind.Warning);
            return;
        }

        IsLaunching = true;
        SetStatus($"正在启动 {installation.Profile.EditionLabel}…");
        _logger.Info($"Launch requested for {installation.Profile.EditionLabel} at {installation.GameDirectory}");

        try
        {
            var result = string.IsNullOrWhiteSpace(installation.ExecutablePath)
                ? await _launcher.LaunchViaSteamAsync(installation.Profile)
                : await _launcher.LaunchDirectAsync(installation);

            SetStatus(
                result.Success
                    ? $"{result.Message} 如果游戏窗口没有出现，请查看详细日志。"
                    : result.Message,
                result.Success ? StatusKind.Success : StatusKind.Error);
            _logger.Info($"Launch result: {result.Success} — {result.Message}");
        }
        catch (Exception exception)
        {
            _logger.Error("Launch failed", exception);
            SetStatus($"启动失败：{exception.Message}", StatusKind.Error);
        }
        finally
        {
            IsLaunching = false;
        }
    }

    [RelayCommand]
    private async Task OpenGameFolderAsync()
    {
        if (Game.Selected is not { } installation)
        {
            SetStatus("尚未选择游戏安装。", StatusKind.Warning);
            return;
        }

        var result = await _launcher.OpenGameFolderAsync(installation);
        SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
    }

    partial void OnSelectedNavigationChanged(NavigationItem? value)
    {
        if (value is null) return;
        CurrentPage = _pages.GetValueOrDefault(value.Key);
        _ = ActivateCurrentPageAsync();
    }

    private async Task ActivateCurrentPageAsync()
    {
        if (CurrentPage is IAsyncPage page)
        {
            try
            {
                await page.OnActivatedAsync();
            }
            catch (Exception exception)
            {
                _logger.Error("页面初始化失败", exception);
                SetStatus($"页面初始化失败：{exception.Message}", StatusKind.Error);
            }
        }
    }
}
