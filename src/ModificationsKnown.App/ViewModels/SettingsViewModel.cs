using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.App.ViewModels;

public partial class SettingsViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly IAppSettingsService _settings;
    private readonly IGameLocator _locator;
    private readonly INexusModsClient _nexus;
    private readonly IFilePickerService _filePicker;
    private readonly IAppLogger _logger;

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

    public SettingsViewModel(
        IAppShell shell,
        IAppSettingsService settings,
        IGameLocator locator,
        INexusModsClient nexus,
        IFilePickerService filePicker,
        IAppLogger logger)
    {
        _shell = shell;
        _settings = settings;
        _locator = locator;
        _nexus = nexus;
        _filePicker = filePicker;
        _logger = logger;
        LoadFromSettings();
    }

    public string SettingsPath => "设置保存在当前用户的 AppData 目录；API Key 不会写入日志。";

    public string LogDirectory => _logger.LogDirectory;

    public Task OnActivatedAsync()
    {
        LoadFromSettings();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task BrowseGameAsync()
    {
        var path = await _filePicker.PickExecutableAsync();
        if (!string.IsNullOrWhiteSpace(path)) GamePath = path;
    }

    [RelayCommand]
    private async Task BrowseGameFolderAsync()
    {
        var path = await _filePicker.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path)) GamePath = path;
    }

    [RelayCommand]
    private async Task DetectGameAsync()
    {
        if (string.IsNullOrWhiteSpace(GamePath))
        {
            _shell.SetStatus("请先填写游戏可执行文件或游戏目录。", StatusKind.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var installation = await _locator.InspectAsync(GamePath);
            if (installation is null)
            {
                _shell.SetStatus("没有在该路径找到《未知伤亡》的游戏数据目录。", StatusKind.Error);
                return;
            }

            GamePath = installation.GameDirectory;
            _settings.Current.GamePath = GamePath;
            await _shell.RefreshGameAsync();
            _shell.SetStatus($"已识别到 {installation.Profile.EditionLabel}。", StatusKind.Success);
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"检测失败：{exception.Message}", StatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            _settings.Current.GamePath = string.IsNullOrWhiteSpace(GamePath) ? null : GamePath.Trim();
            _settings.Current.NexusApiKey = NexusApiKey.Trim();
            _settings.Current.NexusGameDomain = string.IsNullOrWhiteSpace(NexusDomain)
                ? "scavprototype"
                : NexusDomain.Trim();
            _settings.Current.PageSize = Math.Clamp(PageSize, 1, 100);
            _settings.Current.IncludeAdultMods = IncludeAdultMods;
            _settings.Current.ConfirmBeforeDelete = ConfirmBeforeDelete;
            await _settings.SaveAsync();
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

    private void LoadFromSettings()
    {
        GamePath = _settings.Current.GamePath ?? string.Empty;
        NexusApiKey = _settings.Current.NexusApiKey;
        NexusDomain = _settings.Current.NexusGameDomain;
        PageSize = _settings.Current.PageSize;
        IncludeAdultMods = _settings.Current.IncludeAdultMods;
        ConfirmBeforeDelete = _settings.Current.ConfirmBeforeDelete;
    }
}
