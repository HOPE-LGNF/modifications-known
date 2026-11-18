using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Saves;
using ModificationsKnown.Infrastructure.Storage;
using ModificationsKnown.Infrastructure.Windows;

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
    private readonly ActivityLogService _activity;
    private readonly SaveGroupService _saveGroups;
    private readonly GameProcessMonitor _processMonitor;
    private readonly NxmProtocolRegistrar _nxmRegistrar;
    private readonly IUserDialogService _dialogs;
    private readonly ModGroupStore _groups = new();
    private DateTimeOffset _lastAccountCheck;
    private int _modCount;

    [ObservableProperty]
    private bool _hasGame;

    [ObservableProperty]
    private string _gameTitle = "尚未检测到游戏";

    [ObservableProperty]
    private string _gameSubtitle = "安装游戏后，启动器会自动找到正式版或试玩版。";

    [ObservableProperty]
    private string _gamePath = "—";

    [ObservableProperty]
    private bool _isBusy;

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
    private bool _nexusConfigured;

    [ObservableProperty]
    private string _lastLaunchLabel = "尚未通过 MK 启动";

    [ObservableProperty]
    private string _saveIsolationLabel = "未检测存档隔离";

    [ObservableProperty]
    private string _saveProfilePath = "—";

    [ObservableProperty]
    private string _saveGroupLabel = "未创建";

    [ObservableProperty]
    private string _dataRoot = "—";

    [ObservableProperty]
    private string _currentGroupLabel = "—";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _runningLabel = "未运行";

    public ObservableCollection<HomeChecklistItem> Checklist { get; } = [];

    public ObservableCollection<ActivityEntry> RecentActivity { get; } = [];

    public bool CanLaunch => _shell.CanLaunch;

    public string LaunchButtonText => _shell.LaunchButtonText;

    public bool IsCloseAction => _shell.IsCloseAction;

    [RelayCommand]
    private Task LaunchGame() => _shell.LaunchGameAsync();

    [RelayCommand]
    private void ChecklistAction(HomeChecklistItem? item)
    {
        if (item is null) return;

        if (string.Equals(item.TargetPage, "nxm", StringComparison.OrdinalIgnoreCase))
        {
            _shell.NavigateSettings("nxm");
            return;
        }

        if (string.Equals(item.TargetPage, "settings#bepinex", StringComparison.OrdinalIgnoreCase))
        {
            _shell.NavigateSettings("bepinex");
            return;
        }

        _shell.NavigateTo(item.TargetPage);
    }

    public bool HasActivity => RecentActivity.Count > 0;

    public bool HasNoActivity => !HasActivity;

    public HomeViewModel(
        IAppShell shell,
        IGameLocator locator,
        IModManager modManager,
        IGameLauncher launcher,
        IAppSettingsService settings,
        INexusModsClient nexus,
        ActivityLogService activity,
        GameProcessMonitor processMonitor,
        NxmProtocolRegistrar nxmRegistrar,
        IUserDialogService dialogs,
        SaveGroupService saveGroups)
    {
        _shell = shell;
        _locator = locator;
        _modManager = modManager;
        _launcher = launcher;
        _settings = settings;
        _nexus = nexus;
        _activity = activity;
        _saveGroups = saveGroups;
        _processMonitor = processMonitor;
        _nxmRegistrar = nxmRegistrar;
        _dialogs = dialogs;
        _shell.Game.Changed += OnGameChanged;
        _activity.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        _processMonitor.Changed += (_, _) => Dispatcher.UIThread.Post(UpdateRunningState);

        if (_shell is System.ComponentModel.INotifyPropertyChanged notifier)
        {
            notifier.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(IAppShell.CanLaunch) or nameof(IAppShell.LaunchButtonText))
                {
                    OnPropertyChanged(nameof(CanLaunch));
                    OnPropertyChanged(nameof(LaunchButtonText));
                    OnPropertyChanged(nameof(IsCloseAction));
                }
            };
        }
    }

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            DataRoot = AppPaths.RootDirectory;
            LoadActivity();

            if (_shell.Game.Selected is not { } installation)
            {
                HasGame = false;
                GameTitle = "尚未检测到游戏";
                GameSubtitle = "请在设置中添加游戏实例，或安装 Steam 版《未知伤亡》。";
                GamePath = "—";
                BepInExReady = false;
                SetModCounts(0, 0);
                NexusConfigured = !string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey);
                LastLaunchLabel = "尚未通过 MK 启动";
                SaveIsolationLabel = "未选择版本";
                SaveProfilePath = "—";
                SaveGroupLabel = "未创建";
                CurrentGroupLabel = "—";
                IsRunning = false;
                RunningLabel = "未运行";
                await RefreshNexusAccountAsync();
                return;
            }

            HasGame = true;
            GameTitle = installation.DisplayLabel;
            GameSubtitle = $"{installation.Profile.EditionLabel} · {installation.Profile.SteamAppId}";
            GamePath = installation.GameDirectory;
            BepInExReady = installation.HasBepInEx;
            NexusConfigured = !string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey);
            LastLaunchLabel = FormatLastLaunch(_settings.Current.LastLaunchAt);
            var currentSaveGroup = _saveGroups.GetLiveGroup(installation);
            SaveGroupLabel = currentSaveGroup?.Name ?? "未指定";
            SaveProfilePath = currentSaveGroup?.DirectoryPath ?? "—";
            SaveIsolationLabel = DescribeIsolation(installation);
            CurrentGroupLabel = DescribeCurrentGroup(installation.GameDirectory);
            UpdateRunningState();
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

    private void UpdateRunningState()
    {
        if (_shell.Game.Selected is not { } installation)
        {
            IsRunning = false;
            RunningLabel = "未运行";
            return;
        }

        var count = _processMonitor.Count(installation);
        IsRunning = count > 0;
        RunningLabel = count switch
        {
            0 => "未运行",
            1 => "运行中",
            _ => $"运行中 · {count} 个进程"
        };
    }

    private void LoadActivity()
    {
        RecentActivity.Clear();
        foreach (var entry in _activity.Recent(4))
            RecentActivity.Add(entry);
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(HasNoActivity));
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
            BepInExReady ? "BepInEx 已就绪" : "在设置的版本列表里点 ⋯ 安装",
            BepInExReady, "去安装", "settings#bepinex"));
        Checklist.Add(new(3, "配置 Nexus API Key",
            NexusConfigured ? NexusAccountDetail : "浏览可匿名，查看文件和下载需要 API Key",
            NexusConfigured, "去设置", "settings"));

        var nxmStatus = _nxmRegistrar.GetStatus();
        var nxmRegistered = nxmStatus.RegisteredForCurrentExecutable;
        Checklist.Add(new(4, "注册 NXM 链接",
            nxmRegistered
                ? "NXM 已注册，浏览器下载可自动接管"
                : nxmStatus.Supported
                    ? "免费账号下载模组需要；注册后可接收浏览器下载"
                    : "当前平台不支持 NXM 协议",
            nxmRegistered || !nxmStatus.Supported,
            "去注册",
            "nxm"));

        Checklist.Add(new(5, "安装第一个模组",
            _modCount > 0 ? $"已安装 {_modCount} 个包" : "到模组发现浏览，或把压缩包拖进窗口",
            _modCount > 0, "去模组发现", "discover"));
    }

    private string DescribeIsolation(GameInstallation installation)
    {
        if (!OperatingSystem.IsWindows()) return "非 Windows：跳过存档隔离";

        try
        {
            var preview = _saveGroups.GetLaunchPreview(installation);
            if (preview.NeedsInitialization || preview.Group is null)
                return "尚未创建存档组";

            var live = _saveGroups.GetLiveGroup(installation);
            if (live is null)
                return "当前链接未指向存档组";
            if (live.Id != preview.Group.Id)
                return $"链接指向「{live.Name}」，选择为「{preview.Group.Name}」";
            return $"已指向「{live.Name}」";
        }
        catch
        {
            return "存档组状态未知";
        }
    }

    private string DescribeCurrentGroup(string gameDirectory)
    {
        try
        {
            var groups = _groups.Get(gameDirectory);
            return groups.FirstOrDefault(group => group.IsDefault)?.Name
                   ?? groups.FirstOrDefault()?.Name
                   ?? "未创建";
        }
        catch
        {
            return "未创建";
        }
    }

    private static string FormatLastLaunch(DateTimeOffset? timestamp)
    {
        if (timestamp is null) return "尚未通过 MK 启动";

        var delta = DateTimeOffset.Now - timestamp.Value;
        if (delta < TimeSpan.FromMinutes(1)) return "刚刚启动";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes} 分钟前启动";
        if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours} 小时前启动";
        if (delta < TimeSpan.FromDays(2)) return "昨天启动";
        return timestamp.Value.ToLocalTime().ToString("MM-dd HH:mm") + " 启动";
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
    private Task RefreshGameAsync() => _shell.RefreshGameAsync();

    private void OnGameChanged(object? sender, EventArgs e) => _ = RefreshAsync();
}
