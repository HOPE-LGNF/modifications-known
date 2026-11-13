using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Installation;
using ModificationsKnown.Infrastructure.Windows;

namespace ModificationsKnown.App.ViewModels;

public partial class SettingsViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly IAppSettingsService _settings;
    private readonly IGameLocator _locator;
    private readonly INexusModsClient _nexus;
    private readonly IFilePickerService _filePicker;
    private readonly IAppLogger _logger;
    private readonly NxmProtocolRegistrar _nxmRegistrar;
    private readonly IUserDialogService _dialogs;
    private readonly GameInstanceStore _instances;
    private readonly InstanceDeletionService _deletion;
    private readonly DataLocationService _dataLocation = new();
    private readonly BepInExInstaller _bepInExInstaller;
    private bool _loadingSettings;

    [ObservableProperty]
    private string _gamePath = string.Empty;

    [ObservableProperty]
    private string _nexusApiKey = string.Empty;

    [ObservableProperty]
    private string _nexusDomain = "scavprototype";

    [ObservableProperty]
    private int _pageSize = 24;

    [ObservableProperty]
    private bool _includeAdultMods;

    [ObservableProperty]
    private bool _confirmBeforeDelete = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _connectionStatus = "尚未测试。";

    [ObservableProperty]
    private bool _nxmProtocolSupported;

    [ObservableProperty]
    private bool _nxmProtocolRegistered;

    [ObservableProperty]
    private string _nxmProtocolStatus = "正在读取…";

    [ObservableProperty]
    private string _accountStatus = "尚未检查账号类型。";

    [ObservableProperty]
    private string _dataRoot = string.Empty;

    [ObservableProperty]
    private bool _isCustomDataRoot;

    [ObservableProperty]
    private string _instanceHint = string.Empty;

    public ObservableCollection<GameVersionRow> GameVersions { get; } = [];

    private static readonly HashSet<string> EditableSettings =
    [
        nameof(GamePath),
        nameof(NexusApiKey),
        nameof(NexusDomain),
        nameof(PageSize),
        nameof(IncludeAdultMods),
        nameof(ConfirmBeforeDelete)
    ];

    public bool HasUnsavedChanges
    {
        get
        {
            var saved = _settings.Current;
            return NormalizeGamePath(GamePath) != saved.GamePath
                   || NexusApiKey.Trim() != (saved.NexusApiKey ?? string.Empty)
                   || NormalizeDomain(NexusDomain) != saved.NexusGameDomain
                   || Math.Clamp(PageSize, 1, 100) != saved.PageSize
                   || IncludeAdultMods != saved.IncludeAdultMods
                   || ConfirmBeforeDelete != saved.ConfirmBeforeDelete;
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not null && EditableSettings.Contains(e.PropertyName))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasUnsavedChanges)));
    }

    private static string? NormalizeGamePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    private static string NormalizeDomain(string domain) =>
        string.IsNullOrWhiteSpace(domain) ? "scavprototype" : domain.Trim();

    partial void OnConfirmBeforeDeleteChanged(bool value)
    {
        if (_loadingSettings || value) return;
        _ = WarnAboutDisablingDeleteConfirmationAsync();
    }

    public SettingsViewModel(
        IAppShell shell,
        IAppSettingsService settings,
        IGameLocator locator,
        INexusModsClient nexus,
        IFilePickerService filePicker,
        IAppLogger logger,
        NxmProtocolRegistrar nxmRegistrar,
        IUserDialogService dialogs,
        GameInstanceStore instances,
        InstanceDeletionService deletion)
    {
        _shell = shell;
        _settings = settings;
        _locator = locator;
        _nexus = nexus;
        _filePicker = filePicker;
        _logger = logger;
        _nxmRegistrar = nxmRegistrar;
        _dialogs = dialogs;
        _instances = instances;
        _deletion = deletion;
        _bepInExInstaller = new BepInExInstaller(logger);
        _shell.Game.Changed += OnGameChanged;
        DataRoot = _dataLocation.CurrentRoot;
        IsCustomDataRoot = _dataLocation.IsCustom;
        LoadFromSettings();
        RefreshNxmStatus();
        RefreshGameVersions();
    }

    public string SettingsPath => "设置保存在当前用户的 AppData 目录；API Key 不会写入日志。";

    public string LogDirectory => _logger.LogDirectory;

    public Task OnActivatedAsync()
    {
        if (!HasUnsavedChanges) LoadFromSettings();
        RefreshNxmStatus();
        RefreshGameVersions();
        DataRoot = _dataLocation.CurrentRoot;
        IsCustomDataRoot = _dataLocation.IsCustom;
        return Task.CompletedTask;
    }

    private void RefreshGameVersions()
    {
        GameVersions.Clear();
        foreach (var installation in _shell.Game.Installations
                     .OrderByDescending(installation => installation.Profile.Edition)
                     .ThenBy(installation => installation.DisplayLabel, StringComparer.OrdinalIgnoreCase))
        {
            GameVersions.Add(new GameVersionRow(
                installation,
                string.Equals(_shell.Game.Selected?.GameDirectory, installation.GameDirectory, StringComparison.OrdinalIgnoreCase),
                SaveVersionAliasAsync,
                InstallLoaderAsync,
                OpenBepInExDownloadAsync,
                DeleteVersionAsync));
        }

        var current = _shell.Game.Selected;
        InstanceHint = GameVersions.Count == 0
            ? "还没有添加版本。点“添加实例”选择游戏目录或可执行文件。"
            : current is null
                ? $"{GameVersions.Count} 个版本；用顶部下拉栏选择当前版本。"
                : $"{GameVersions.Count} 个版本；当前：{current.DisplayLabel}";
    }

    private void OnGameChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshGameVersions);

    private async Task SaveVersionAliasAsync(GameVersionRow row)
    {
        var alias = row.Alias.Trim();
        if (string.IsNullOrWhiteSpace(alias))
        {
            _shell.SetStatus("版本名称不能为空。", StatusKind.Warning);
            return;
        }

        _shell.Game.Rename(row.Path, alias);
        await _settings.SaveAsync();
        RefreshGameVersions();
        _shell.SetStatus($"版本已重命名为「{alias}」。", StatusKind.Success);
    }

    private async Task DeleteVersionAsync(GameVersionRow row)
    {
        var installation = row.Installation;
        if (InstanceDeletionService.IsInstanceRunning(installation))
        {
            await _dialogs.AlertAsync(
                "无法删除版本",
                $"「{installation.DisplayLabel}」正在运行。\n\n请先关闭游戏，再回来删除；本次没有做任何修改。");
            return;
        }

        var profile = _deletion.GetProfileDirectory(installation);
        var saves = _deletion.ListSaves(installation);
        var plan = await _dialogs.PickInstanceDeletionAsync(
            installation,
            saves,
            profile,
            _deletion.SuggestSaveTarget(installation));
        if (plan is null) return;

        if (InstanceDeletionService.IsInstanceRunning(installation))
        {
            await _dialogs.AlertAsync(
                "无法删除版本",
                "实例在确认过程中启动了。请先关闭游戏；本次没有做任何修改。");
            return;
        }

        IsBusy = true;
        try
        {
            var report = await _deletion.ExecuteAsync(installation, plan);
            var lines = report.Steps
                .Select(step => $"{(step.Success ? "成功" : "失败")} · {step.Name}：{step.Message}")
                .ToList();
            var message = string.Join(Environment.NewLine, lines);
            if (!string.IsNullOrWhiteSpace(message)) message = Environment.NewLine + message;

            await _dialogs.AlertAsync(
                report.Aborted ? "删除已中止" : report.Success ? "版本已删除" : "删除完成，但有失败项",
                report.Summary + message,
                "知道了");

            if (!report.Aborted)
            {
                var wasSelected = string.Equals(
                    _shell.Game.Selected?.GameDirectory,
                    installation.GameDirectory,
                    StringComparison.OrdinalIgnoreCase);
                _shell.Game.Remove(installation.GameDirectory);

                if (wasSelected)
                {
                    _shell.Settings.Current.PreferredGamePath = _shell.Game.Selected?.GameDirectory;
                    _shell.Settings.Current.PreferredInstallationId = _shell.Game.Selected?.Profile.Id;
                    await _shell.Settings.SaveAsync();
                }

                RefreshGameVersions();
                _shell.SetStatus(
                    report.Success ? $"已删除版本「{installation.DisplayLabel}」。" : report.Summary,
                    report.Success ? StatusKind.Success : StatusKind.Warning);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddInstanceAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        IsBusy = true;
        try
        {
            var installation = await _shell.Game.AddPathAsync(path, _locator);
            if (installation is null)
            {
                _shell.SetStatus("没有在该路径找到《未知伤亡》的游戏数据目录。", StatusKind.Error);
                return;
            }

            GamePath = installation.GameDirectory;
            _settings.Current.GamePath = installation.GameDirectory;
            _settings.Current.PreferredGamePath = installation.GameDirectory;
            _settings.Current.PreferredInstallationId = installation.Profile.Id;
            await _settings.SaveAsync();
            RefreshGameVersions();
            _shell.SetStatus($"已添加版本「{installation.DisplayLabel}」。", StatusKind.Success);
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"添加实例失败：{exception.Message}", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddInstanceFromFileAsync()
    {
        var path = await _filePicker.PickExecutableAsync();
        await AddInstanceAsync(path);
    }

    [RelayCommand]
    private async Task AddInstanceFromFolderAsync()
    {
        var path = await _filePicker.PickFolderAsync();
        await AddInstanceAsync(path);
    }

    [RelayCommand]
    private async Task RediscoverInstancesAsync()
    {
        IsBusy = true;
        try
        {
            await _shell.RefreshGameAsync();
            RefreshGameVersions();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallLoaderAsync(GameVersionRow row)
    {
        if (row.Installation.HasBepInEx)
        {
            _shell.SetStatus("这个版本已经安装了 BepInEx。", StatusKind.Info);
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "安装 BepInEx",
            $"将为「{row.Installation.DisplayLabel}」安装 BepInEx。\n\n请选择与本游戏版本匹配的 BepInEx 压缩包；MK 会先校验布局再复制到游戏目录。",
            "选择压缩包",
            "取消");
        if (!confirmed) return;

        var archive = await _filePicker.PickArchiveAsync();
        if (string.IsNullOrWhiteSpace(archive)) return;

        IsBusy = true;
        try
        {
            var result = await _bepInExInstaller.InstallAsync(row.Path, archive);
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            if (result.Success)
            {
                await _shell.RefreshGameAsync();
                RefreshGameVersions();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task OpenBepInExDownloadAsync(GameVersionRow row)
    {
        // BepInEx 5 is the stable line used by Mono Unity games; /latest skips
        // the 6.x pre-releases, so this resolves to the newest BepInEx 5 build.
        OpenUrl("https://github.com/BepInEx/BepInEx/releases/latest");
        _shell.SetStatus(
            "已打开 BepInEx 最新稳定版；下载 zip 后回到这里，用“本地导入压缩包”安装。",
            StatusKind.Info);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ChangeDataRootAsync()
    {
        var path = await _filePicker.PickDataFolderAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var root = _dataLocation.SetRoot(path);
            DataRoot = root;
            IsCustomDataRoot = _dataLocation.IsCustom;
            await _dialogs.AlertAsync(
                "需要重启",
                $"数据目录已设置为：\n{root}\n\n设置与主题已复制到新目录；日志从下次启动起写入新目录。\n请关闭并重新启动 MK 后生效。",
                "知道了");
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"切换数据目录失败：{exception.Message}", StatusKind.Error);
        }
    }

    [RelayCommand]
    private async Task ResetDataRootAsync()
    {
        try
        {
            _dataLocation.Reset();
            DataRoot = _dataLocation.DefaultRoot;
            IsCustomDataRoot = false;
            await _dialogs.AlertAsync(
                "需要重启",
                $"数据目录已恢复为默认位置：\n{DataRoot}\n\n请关闭并重新启动 MK 后生效。",
                "知道了");
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"恢复默认数据目录失败：{exception.Message}", StatusKind.Error);
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开页面失败：{exception.Message}", StatusKind.Error);
        }
    }

    [RelayCommand]
    private void OpenApiKeyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.nexusmods.com/settings/api-keys")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开 API Key 页面失败：{exception.Message}", StatusKind.Error);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            _settings.Current.GamePath = NormalizeGamePath(GamePath);
            _settings.Current.NexusApiKey = NexusApiKey.Trim();
            _settings.Current.NexusGameDomain = NormalizeDomain(NexusDomain);
            _settings.Current.PageSize = Math.Clamp(PageSize, 1, 100);
            _settings.Current.IncludeAdultMods = IncludeAdultMods;
            _settings.Current.ConfirmBeforeDelete = ConfirmBeforeDelete;
            await _settings.SaveAsync();
            OnPropertyChanged(nameof(HasUnsavedChanges));
            _shell.SetStatus("设置已保存。", StatusKind.Success);
            await _shell.RefreshGameAsync();
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"保存设置失败：{exception.Message}", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestNexusAsync()
    {
        IsBusy = true;
        ConnectionStatus = "正在测试 Nexus API…";
        try
        {
            _settings.Current.NexusApiKey = NexusApiKey.Trim();
            _settings.Current.NexusGameDomain = string.IsNullOrWhiteSpace(NexusDomain)
                ? "scavprototype"
                : NexusDomain.Trim();

            var result = await _nexus.SearchModsAsync(
                _settings.Current.NexusGameDomain,
                new NexusSearchQuery(PageSize: 1, IncludeAdult: IncludeAdultMods));

            ConnectionStatus = $"连接成功，找到 {result.TotalCount} 个模组。";
            _shell.SetStatus(ConnectionStatus, StatusKind.Success);
        }
        catch (NexusApiException exception)
        {
            ConnectionStatus = exception.Message;
            _shell.SetStatus(exception.Message, StatusKind.Error);
        }
        catch (Exception exception)
        {
            ConnectionStatus = $"连接失败：{exception.Message}";
            _shell.SetStatus(ConnectionStatus, StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckAccountAsync()
    {
        IsBusy = true;
        AccountStatus = "正在查询 Nexus 账号…";
        try
        {
            _settings.Current.NexusApiKey = NexusApiKey.Trim();
            var status = await _nexus.GetAccountStatusAsync();
            AccountStatus = $"{status.TierLabel} · {status.QuotaLabel}";
            _shell.SetStatus($"Nexus 账号：{status.TierLabel}", StatusKind.Success);
        }
        catch (NexusApiException exception)
        {
            AccountStatus = exception.Message;
            _shell.SetStatus(exception.Message, StatusKind.Error);
        }
        catch (Exception exception)
        {
            AccountStatus = $"查询失败：{exception.Message}";
            _shell.SetStatus(AccountStatus, StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RegisterNxmProtocolAsync()
    {
        var status = _nxmRegistrar.GetStatus();
        var hasOtherHandler = !status.RegisteredForCurrentExecutable
                              && !string.IsNullOrWhiteSpace(status.CurrentCommand);

        if (hasOtherHandler)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "覆盖 NXM 关联",
                $"当前 nxm:// 由以下程序处理：\n{status.CurrentCommand}\n\n是否让 MK 覆盖它？其他程序的关联会先备份，取消注册时尝试恢复。",
                "覆盖",
                "取消");
            if (!confirmed) return;
        }

        var result = _nxmRegistrar.Register(overwriteExisting: hasOtherHandler);
        _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
        RefreshNxmStatus();
    }

    private async Task WarnAboutDisablingDeleteConfirmationAsync()
    {
        var proceed = await _dialogs.ConfirmAsync(
            "关闭删除确认",
            "关闭后，删除模组将不再弹出二次确认，误删无法从 MK 内恢复。\n\n建议保持开启。",
            "仍然关闭",
            "保持开启");
        if (proceed) return;

        _loadingSettings = true;
        ConfirmBeforeDelete = true;
        _loadingSettings = false;
    }

    [RelayCommand]
    private void UnregisterNxmProtocol()
    {
        var result = _nxmRegistrar.Unregister();
        _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
        RefreshNxmStatus();
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(_logger.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _logger.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开日志目录失败：{exception.Message}", StatusKind.Error);
        }
    }

    private void RefreshNxmStatus()
    {
        var status = _nxmRegistrar.GetStatus();
        NxmProtocolSupported = status.Supported;
        NxmProtocolRegistered = status.RegisteredForCurrentExecutable;
        NxmProtocolStatus = !status.Supported
            ? "当前平台不支持 NXM 协议注册。"
            : status.RegisteredForCurrentExecutable
                ? "已注册到当前启动器；浏览器点击 Mod manager download 会回到这里。"
                : string.IsNullOrWhiteSpace(status.CurrentCommand)
                    ? "尚未注册；只能手动粘贴 NXM 链接。"
                    : $"nxm:// 当前由其他程序处理：{status.CurrentCommand}";
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        LoadFromSettings();
        _shell.SetStatus("已撤销未保存的修改。");
    }

    private void LoadFromSettings()
    {
        _loadingSettings = true;
        try
        {
            GamePath = _settings.Current.GamePath ?? string.Empty;
            NexusApiKey = _settings.Current.NexusApiKey;
            NexusDomain = _settings.Current.NexusGameDomain;
            PageSize = _settings.Current.PageSize;
            IncludeAdultMods = _settings.Current.IncludeAdultMods;
            ConfirmBeforeDelete = _settings.Current.ConfirmBeforeDelete;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

}
