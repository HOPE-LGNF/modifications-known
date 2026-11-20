using System.Reflection;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.App.Theming;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Network;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Diagnostics.Logs;
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
    private readonly GameLogSourceProvider _logSourceProvider;
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
    private bool _isClosing;

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

    public bool CanLaunch => !IsLaunching && !IsClosing;

    public bool IsSelectedInstanceRunning =>
        Game.Selected is { } installation && _processMonitor.IsRunning(installation);

    public bool IsCloseAction =>
        IsClosing || (!IsLaunching && Settings.Current.PreventMultipleInstances && IsSelectedInstanceRunning);

    public string LaunchButtonText =>
        IsClosing ? "关闭中…"
        : IsLaunching ? "正在启动…"
        : IsCloseAction ? "关闭游戏"
        : "启动游戏";

    partial void OnIsLaunchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(IsCloseAction));
    }

    partial void OnIsClosingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(IsCloseAction));
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
        IClipboardService clipboard,
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
        _processMonitor.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsSelectedInstanceRunning));
            OnPropertyChanged(nameof(LaunchButtonText));
            OnPropertyChanged(nameof(IsCloseAction));
        });
        Settings.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(LaunchButtonText));
            OnPropertyChanged(nameof(IsSelectedInstanceRunning));
            OnPropertyChanged(nameof(IsCloseAction));
        };

        NavigationItems =
        [
            new("home", "概览", "游戏状态与快捷启动", "⌂"),
            new("discover", "模组发现", "从 Nexus Mods 浏览与安装", "⌕"),
            new("installed", "已安装模组", "启用、停用与清理本地模组", "▣"),
            new("saves", "存档管理", "各版本的存档与备份", "▤"),
            new("logs", "日志", "BepInEx 与 Unity 日志", "☰"),
            new("appearance", "启动器外观", "主题变量与基础皮肤", "◈"),
            new("settings", "设置", "游戏、版本与 Nexus", "⚙")
        ];

        var nexusMatch = new NexusMatchService(nexus, settings, logger);
        var manualMatches = new NexusManualMatchStore();
        var modGroups = new ModGroupStore();
        var sources = new JsonInstalledSourceStore(logger);
        var operations = new ModOperationService(modManager, installer);
        var updates = new PackageUpdateService(operations, logger);
        var nxmRegistrar = new NxmProtocolRegistrar();
        var deletion = new InstanceDeletionService(
            instanceStore,
            modGroups,
            manualMatches,
            sources,
            _saveIsolation,
            logger);
        _logSourceProvider = new GameLogSourceProvider(Game);
        _pages = new Dictionary<string, object>
        {
            ["home"] = new HomeViewModel(this, locator, modManager, launcher, settings, nexus, _activity, _saveIsolation, _processMonitor, nxmRegistrar, dialogs),
            ["discover"] = new DiscoverViewModel(this, nexus, settings, downloader, operations, thumbnails, sources, nxmRegistrar, dialogs, updates, logger),
            ["installed"] = new InstalledViewModel(this, operations, dialogs, settings, nexusMatch, sources, filePicker, manualMatches, modGroups, nexus, thumbnails, _processMonitor, downloader, updates, nxmRegistrar, logger),
            ["saves"] = new SavesViewModel(this, _saveIsolation, dialogs, filePicker, logger, _processMonitor),
            ["logs"] = new LogViewerViewModel(
                this,
                _logSourceProvider,
                filePicker,
                clipboard,
                new ShellExternalLauncher(),
                new LocalLogReader()),
            ["appearance"] = new LauncherSkinViewModel(this, settings, filePicker, logger),
            ["gameskin"] = new GameSkinViewModel(),
            ["settings"] = new SettingsViewModel(this, settings, locator, nexus, filePicker, logger, nxmRegistrar, dialogs, instanceStore, deletion, _processMonitor)
        };

        SelectedNavigation = NavigationItems[0];
        CurrentPage = _pages["home"];
        Game.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(DropHint));
            OnPropertyChanged(nameof(IsGameSkinOverlayVisible));
            OnPropertyChanged(nameof(IsSelectedInstanceRunning));
            OnPropertyChanged(nameof(LaunchButtonText));
            OnPropertyChanged(nameof(IsCloseAction));
        };
    }

    public IAppSettingsService Settings { get; }

    public GameContext Game { get; }

    public ActivityLogService Activity => _activity;

    public GameProcessMonitor ProcessMonitor => _processMonitor;

    public void Shutdown()
    {
        if (_pages.GetValueOrDefault("logs") is LogViewerViewModel logs) logs.Dispose();
        _logSourceProvider.Dispose();
        _processMonitor.Dispose();
    }

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

    public string DropHint => Game.Selected switch
    {
        null => "当前未选择版本，无法安装",
        { HasBepInEx: false } => "当前版本未安装 BepInEx，无法安装",
        { HasPluginsDirectory: false } => "当前版本没有 BepInEx/plugins，无法安装",
        var installation => $"松开以安装到 {installation.DisplayLabel}"
    };

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
            StatusKind.Success => ThemeService.Current.Accent,
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

    public event Action<IReadOnlyCollection<string>>? ModsChanged;

    public void NotifyModsChanged(IReadOnlyCollection<string> packageNames)
    {
        if (packageNames.Count > 0) ModsChanged?.Invoke(packageNames);
    }

    public async Task HandleNxmLinkAsync(string raw)
    {
        if (_pages.GetValueOrDefault("discover") is not DiscoverViewModel discover)
        {
            SetStatus("模组发现页尚未初始化。", StatusKind.Error);
            return;
        }

        // NXM can arrive while the user is on Installed/Saves/Settings. Never
        // yank them to Discover; the download still runs in the background and
        // reports through the status bar.
        await discover.HandleNxmLinkAsync(raw);
    }

    public void NavigateSettings(string focus)
    {
        if (_pages.TryGetValue("settings", out var page) && page is SettingsViewModel settings)
            settings.RequestFocus(focus);
        NavigateTo("settings");
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
    private async Task CloseGameAsync(GameInstallation installation)
    {
        var confirmed = await _dialogs.ConfirmDangerAsync(
            "关闭游戏",
            $"将结束「{installation.DisplayLabel}」的所有游戏进程。\n\n未保存的游戏进度会丢失，且无法恢复。确定要关闭吗？",
            "结束游戏",
            "取消");
        if (!confirmed) return;

        IsClosing = true;
        try
        {
            SetStatus($"正在关闭「{installation.DisplayLabel}」…", StatusKind.Info);
            var closed = await _processMonitor.CloseInstanceAsync(installation);
            SetStatus(
                closed > 0
                    ? $"已结束「{installation.DisplayLabel}」的 {closed} 个进程。"
                    : "没有找到可结束的进程，游戏可能已经退出。",
                closed > 0 ? StatusKind.Success : StatusKind.Warning);
        }
        catch (Exception exception)
        {
            _logger.Error("Close game failed", exception);
            SetStatus($"关闭游戏失败：{exception.Message}", StatusKind.Error);
        }
        finally
        {
            IsClosing = false;
        }
    }

    public async Task LaunchGameAsync()
    {
        if (IsLaunching || IsClosing) return;

        if (Game.Selected is not { } installation)
        {
            SetStatus("请先检测游戏或手动选择游戏目录。", StatusKind.Warning);
            return;
        }

        if (Settings.Current.PreventMultipleInstances && _processMonitor.IsRunning(installation))
        {
            await CloseGameAsync(installation);
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

            // Steam paths may fall back to the steam:// protocol; manual/portable
            // instances must launch their own executable instead.
            var hasExecutable = !string.IsNullOrWhiteSpace(installation.ExecutablePath)
                                && File.Exists(installation.ExecutablePath);
            var isSteamPath = installation.GameDirectory
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => string.Equals(part, "steamapps", StringComparison.OrdinalIgnoreCase));

            OperationResult result;
            if (isSteamPath)
                result = await _launcher.LaunchViaSteamAsync(installation.Profile);
            else if (hasExecutable)
                result = await _launcher.LaunchDirectAsync(installation);
            else
                result = OperationResult.Fail(
                    "这个版本是非 Steam 实例，但没有找到游戏可执行文件；请在设置中重新选择游戏目录或 exe。");

            if (result.Success)
            {
                SetStatus($"正在等待「{installation.DisplayLabel}」启动…");
                var detected = await _processMonitor.WaitForRunningAsync(
                    installation,
                    TimeSpan.FromSeconds(20));

                if (detected)
                {
                    Settings.Current.LastLaunchAt = DateTimeOffset.Now;
                    await Settings.SaveAsync();
                    _activity.Add("launch", $"启动了「{installation.DisplayLabel}」", result.Message);
                    SetStatus($"「{installation.DisplayLabel}」已启动。", StatusKind.Success);
                }
                else
                {
                    SetStatus(
                        $"{result.Message} 暂未检测到游戏进程；如果游戏窗口已出现，请稍后刷新或查看日志。",
                        StatusKind.Warning);
                }
            }
            else
            {
                SetStatus(result.Message, StatusKind.Error);
            }

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
