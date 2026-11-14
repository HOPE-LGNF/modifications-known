using System.Reflection;
using Avalonia.Media;
using Avalonia.Threading;
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
using ModificationsKnown.Infrastructure.Windows;

namespace ModificationsKnown.App.ViewModels;

public partial class ShellViewModel : ObservableObject, IAppShell
{
    private readonly IAppLogger _logger;
    private readonly IGameLocator _locator;
    private readonly IGameLauncher _launcher;
    private readonly IUserDialogService _dialogs;
    private readonly SaveIsolationService _saveIsolation;
    private readonly ActivityLogService _activity;
    private readonly GameProcessMonitor _processMonitor;
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

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private string _toastMessage = string.Empty;

    [ObservableProperty]
    private IBrush _toastBrush = new SolidColorBrush(Color.Parse("#79C99E"));

    [ObservableProperty]
    private double _toastOpacity;

    private int _toastGeneration;

    private static readonly TimeSpan ToastInfoDuration = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan ToastAlertDuration = TimeSpan.FromSeconds(5);

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

        var instanceStore = new GameInstanceStore();
        Game = new GameContext(instanceStore);
        _saveIsolation = new SaveIsolationService(logger);
        _activity = new ActivityLogService();
        _processMonitor = new GameProcessMonitor(Game, logger);
        _processMonitor.Start();
        NavigationItems =
        [
            new("home", "概览", "游戏状态与快捷启动", "⌂"),
            new("discover", "模组发现", "从 Nexus Mods 浏览与安装", "⌕"),
            new("installed", "已安装模组", "启用、停用与清理本地模组", "▣"),
            new("saves", "存档管理", "各版本的存档与备份", "▤"),
            new("appearance", "启动器外观", "主题变量与基础皮肤", "◈"),
            new("settings", "设置", "游戏、版本与 Nexus", "⚙")
        ];

        var nexusMatch = new NexusMatchService(nexus, settings, logger);
        var manualMatches = new NexusManualMatchStore();
        var modGroups = new ModGroupStore();
        var sources = new JsonInstalledSourceStore(logger);
        var operations = new ModOperationService(modManager, installer);
        var nxmRegistrar = new NxmProtocolRegistrar();
        var deletion = new InstanceDeletionService(
            instanceStore,
            modGroups,
            manualMatches,
            sources,
            _saveIsolation,
            logger);
        _pages = new Dictionary<string, object>
        {
            ["home"] = new HomeViewModel(this, locator, modManager, launcher, settings, nexus, _activity, _saveIsolation, _processMonitor),
            ["discover"] = new DiscoverViewModel(this, nexus, settings, downloader, operations, thumbnails, sources, nxmRegistrar),
            ["installed"] = new InstalledViewModel(this, operations, dialogs, settings, nexusMatch, sources, filePicker, manualMatches, modGroups, nexus, thumbnails, _processMonitor),
            ["saves"] = new SavesViewModel(this, _saveIsolation, dialogs, filePicker, logger, _processMonitor),
            ["appearance"] = new LauncherSkinViewModel(this, settings, filePicker, logger),
            ["gameskin"] = new GameSkinViewModel(),
            ["settings"] = new SettingsViewModel(this, settings, locator, nexus, filePicker, logger, nxmRegistrar, dialogs, instanceStore, deletion, _processMonitor)
        };

        SelectedNavigation = NavigationItems[0];
        CurrentPage = _pages["home"];
        Game.Changed += (_, _) => OnPropertyChanged(nameof(CurrentGameLabel));
    }

    public IAppSettingsService Settings { get; }

    public GameContext Game { get; }

    public ActivityLogService Activity => _activity;

    public GameProcessMonitor ProcessMonitor => _processMonitor;

    public void Shutdown() => _processMonitor.Dispose();

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public string CurrentGameLabel => Game.Selected is null
        ? "未检测到游戏"
        : Game.Selected.DisplayLabel;

    /// <summary>The game-skin page is a placeholder; its veil lives in
    /// MainWindow so it also covers the header and the status bar.</summary>
    public bool IsGameSkinOverlayVisible => SelectedNavigation?.Key == "gameskin";

    public string FooterText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var label = version is null ? "v0.1.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
            return $"{label} · Apache-2.0";
        }
    }

    public string DropHint => Game.Selected is { HasPluginsDirectory: true } installation
        ? $"松开以安装到 {installation.DisplayLabel}"
        : "当前版本没有 BepInEx/plugins，无法安装";

    public static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];

    public async Task InstallDroppedArchivesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (_pages.GetValueOrDefault("installed") is not InstalledViewModel installed) return;

        if (Game.Selected is not { HasPluginsDirectory: true } installation)
        {
            SetStatus(DropHint, StatusKind.Warning);
            return;
        }

        var names = string.Join(Environment.NewLine, paths.Select(path => "· " + Path.GetFileName(path)));
        var confirmed = await _dialogs.ConfirmAsync(
            "安装拖入的压缩包",
            $"将把 {paths.Count} 个压缩包安装到「{installation.DisplayLabel}」：\n\n{names}\n\n安装前会逐个校验压缩包路径；完成后可在“已安装”中启用。",
            "安装",
            "取消");
        if (!confirmed) return;

        NavigateTo("installed");
        await installed.InstallArchivesAsync(paths);
    }

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
        SetStatus("正在加载版本列表…");
        await LoadInstancesAsync(rediscover: false);
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

        if (kind is not StatusKind.Info
            && !message.StartsWith("正在", StringComparison.Ordinal)
            && !message.StartsWith("准备", StringComparison.Ordinal)
            && !message.StartsWith("已检测到", StringComparison.Ordinal))
        {
            _activity.Add(kind == StatusKind.Error ? "error" : "success", message);
        }

        ShowToast(message, kind);
    }

    private async void ShowToast(string message, StatusKind kind)
    {
        var generation = ++_toastGeneration;
        ToastMessage = message;
        ToastBrush = StatusBrush;
        ToastOpacity = 0;
        IsToastVisible = true;

        // Let the hidden state render once, then fade/slide in.
        await Task.Delay(16);
        if (generation != _toastGeneration) return;
        ToastOpacity = 1;

        await Task.Delay(kind is StatusKind.Error or StatusKind.Warning ? ToastAlertDuration : ToastInfoDuration);
        if (generation != _toastGeneration) return;
        ToastOpacity = 0;

        await Task.Delay(240);
        if (generation == _toastGeneration) IsToastVisible = false;
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

    public Task RefreshGameAsync() => LoadInstancesAsync(rediscover: true);

    private async Task LoadInstancesAsync(bool rediscover)
    {
        IsBusy = true;
        try
        {
            if (rediscover)
                await Game.RefreshAsync(_locator, Settings.Current.PreferredGamePath, Settings.Current.PreferredInstallationId);
            else
                await Game.InitializeAsync(_locator, Settings.Current.PreferredGamePath, Settings.Current.PreferredInstallationId);

            if (Game.Selected is null)
            {
                SetStatus(
                    rediscover
                        ? "没有检测到《未知伤亡》。可以在设置中点击“添加实例”。"
                        : "还没有添加版本。请在设置中点击“添加实例”。",
                    rediscover ? StatusKind.Warning : StatusKind.Info);
                return;
            }

            Settings.Current.PreferredInstallationId = Game.Selected.Profile.Id;
            Settings.Current.PreferredGamePath = Game.Selected.GameDirectory;
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
        try
        {
            var saveIsolation = await _saveIsolation.PrepareAsync(installation);
            if (!saveIsolation.Success)
            {
                SetStatus(saveIsolation.Message, StatusKind.Error);
                return;
            }

            SetStatus($"正在启动 {installation.DisplayLabel}…");
            _logger.Info($"Launch requested for {installation.Profile.EditionLabel} at {installation.GameDirectory}");

            var result = string.IsNullOrWhiteSpace(installation.ExecutablePath)
                ? await _launcher.LaunchViaSteamAsync(installation.Profile)
                : await _launcher.LaunchDirectAsync(installation);

            if (result.Success)
            {
                Settings.Current.LastLaunchAt = DateTimeOffset.Now;
                await Settings.SaveAsync();
                _activity.Add("launch", $"启动了「{installation.DisplayLabel}」", result.Message);
            }

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
        OnPropertyChanged(nameof(IsGameSkinOverlayVisible));
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
