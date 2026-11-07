using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.App.ViewModels;

public sealed partial class InstalledModItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isDetailed = true;

    [ObservableProperty]
    private NexusMatch? _nexusMatch;

    public bool IsNexusMatched => NexusMatch is not null;

    public string SourceLabel => NexusMatch is null ? "本地模组" : "Nexus 模组";

    public string NexusDomain { get; set; } = "scavprototype";

    public string NexusName => NexusMatch?.Name ?? "本地模组";

    public string NexusUrl => NexusMatch is null
        ? string.Empty
        : NexusMatch.PageUrl(NexusDomain);

    partial void OnNexusMatchChanged(NexusMatch? value)
    {
        OnPropertyChanged(nameof(IsNexusMatched));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(NexusName));
        OnPropertyChanged(nameof(NexusUrl));
    }

    public InstalledModItemViewModel(InstalledMod mod, Func<InstalledMod, Task> toggle, Func<InstalledMod, Task> delete)
    {
        Mod = mod;
        ToggleCommand = new AsyncRelayCommand(() => toggle(Mod));
        DeleteCommand = new AsyncRelayCommand(() => delete(Mod));
    }

    public InstalledMod Mod { get; }

    public string Name => Mod.Name;

    public string Version => Mod.VersionLabel;

    public string State => Mod.StateLabel;

    public bool IsDisabled => !Mod.IsEnabled;

    public string ToggleText => Mod.IsEnabled ? "禁用" : "启用";

    public string PackagePath => Mod.DirectoryPath;

    public IAsyncRelayCommand OpenNexusCommand => new AsyncRelayCommand(OpenNexusAsync);

    private async Task OpenNexusAsync()
    {
        if (NexusMatch is null) return;

        try
        {
            var url = NexusMatch.PageUrl(NexusDomain);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            await Task.CompletedTask;
        }
        catch
        {
            // The parent page owns user-facing error reporting.
        }
    }

    public string PluginLabel => Mod.PluginCount <= 1 ? "1 个 DLL" : $"{Mod.PluginCount} 个 DLL";

    public string DependencyLabel => Mod.Dependencies.Count == 0
        ? "无声明依赖"
        : $"{Mod.Dependencies.Count} 个依赖";

    public bool CanDelete => Mod.CanDelete;

    public IAsyncRelayCommand ToggleCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }
}

public partial class InstalledViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly IModManager _modManager;
    private readonly IUserDialogService _dialogs;
    private readonly IAppSettingsService _settings;
    private readonly NexusMatchService _nexusMatch;
    private readonly IInstalledSourceStore _sources;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasGame;

    [ObservableProperty]
    private bool _bepInExMissing;

    [ObservableProperty]
    private string _countLabel = "0";

    [ObservableProperty]
    private string _enabledLabel = "0";

    [ObservableProperty]
    private string _emptyMessage = "尚未选择游戏安装。";

    [ObservableProperty]
    private string _pluginsPath = "—";

    [ObservableProperty]
    private bool _isCompactView;

    public bool IsDetailedView => !IsCompactView;

    public string ViewModeButtonText => IsCompactView ? "详细列表" : "紧凑列表";

    partial void OnIsCompactViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDetailedView));
        OnPropertyChanged(nameof(ViewModeButtonText));

        foreach (var item in Mods)
            item.IsDetailed = !value;
    }

    public InstalledViewModel(
        IAppShell shell,
        IModManager modManager,
        IUserDialogService dialogs,
        IAppSettingsService settings,
        NexusMatchService nexusMatch,
        IInstalledSourceStore sources)
    {
        _shell = shell;
        _modManager = modManager;
        _dialogs = dialogs;
        _settings = settings;
        _nexusMatch = nexusMatch;
        _sources = sources;
        _shell.Game.Changed += (_, _) => _ = RefreshAsync();
    }

    public ObservableCollection<InstalledModItemViewModel> Mods { get; } = [];

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Mods.Clear();
            var installation = _shell.Game.Selected;

            if (installation is null)
            {
                HasGame = false;
                BepInExMissing = false;
                EmptyMessage = "尚未选择游戏安装。请先在设置中检测游戏。";
                CountLabel = "0";
                EnabledLabel = "0";
                PluginsPath = "—";
                return;
            }

            HasGame = true;
            PluginsPath = installation.PluginsDirectory;
            BepInExMissing = !installation.HasPluginsDirectory;

            if (!installation.HasPluginsDirectory)
            {
                EmptyMessage = "没有找到 BepInEx/plugins。请先安装 BepInEx。";
                CountLabel = "0";
                EnabledLabel = "0";
                return;
            }

            var mods = await _modManager.ScanAsync(installation);
            foreach (var mod in mods)
                Mods.Add(CreateItem(mod));

            CountLabel = mods.Count.ToString();
            EnabledLabel = mods.Count(mod => mod.IsEnabled).ToString();
            EmptyMessage = mods.Count == 0
                ? "插件目录还是空的。去「模组发现」看看，或手动放入 BepInEx/plugins。"
                : string.Empty;

            _ = MatchNexusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleViewMode() => IsCompactView = !IsCompactView;

    [RelayCommand]
    private Task OpenPluginsFolderAsync()
    {
        if (_shell.Game.Selected is not { } installation
            || !Directory.Exists(installation.PluginsDirectory))
        {
            _shell.SetStatus("BepInEx/plugins 目录不存在。", StatusKind.Warning);
            return Task.CompletedTask;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installation.PluginsDirectory,
                UseShellExecute = true
            });
            _shell.SetStatus("已打开插件目录。", StatusKind.Success);
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开目录失败：{exception.Message}", StatusKind.Error);
            return Task.CompletedTask;
        }
    }

    private async Task ToggleAsync(InstalledMod mod)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _modManager.SetEnabledAsync(mod, !mod.IsEnabled);
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync(InstalledMod mod)
    {
        if (IsBusy) return;
        if (!mod.CanDelete)
        {
            _shell.SetStatus("根目录 DLL 不允许从这里删除。", StatusKind.Warning);
            return;
        }

        if (_settings.Current.ConfirmBeforeDelete)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "删除模组",
                $"将删除整个模组目录：\n{mod.DirectoryPath}\n\n此操作不可恢复。",
                "删除",
                "取消");
            if (!confirmed) return;
        }

        IsBusy = true;
        try
        {
            var result = await _modManager.DeleteAsync(mod);
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private InstalledModItemViewModel CreateItem(InstalledMod mod)
    {
        var installation = _shell.Game.Selected;
        var source = installation is null ? null : _sources.Get(installation.GameDirectory, mod.PackageId);
        var match = source is null
            ? null
            : new NexusMatch(source.NexusName, source.GameScopedId, source.ModUid);

        return new InstalledModItemViewModel(mod, ToggleAsync, DeleteAsync)
        {
            IsDetailed = IsDetailedView,
            NexusDomain = _settings.Current.NexusGameDomain,
            NexusMatch = match
        };
    }

    private async Task RefreshCoreAsync()
    {
        var installation = _shell.Game.Selected;
        if (installation is null || !installation.HasPluginsDirectory)
        {
            await RefreshAsync();
            return;
        }

        var mods = await _modManager.ScanAsync(installation);
        Mods.Clear();
        foreach (var mod in mods)
            Mods.Add(CreateItem(mod));

        CountLabel = mods.Count.ToString();
        EnabledLabel = mods.Count(mod => mod.IsEnabled).ToString();
        EmptyMessage = mods.Count == 0 ? "插件目录还是空的。" : string.Empty;
        _ = MatchNexusAsync();
    }

    private async Task MatchNexusAsync()
    {
        foreach (var item in Mods.ToList())
        {
            if (item.IsNexusMatched) continue;

            var match = await _nexusMatch.ResolveAsync(
                item.Mod.PackageId,
                item.Name,
                item.Mod.Version);
            if (match is not null)
                item.NexusMatch = match;
        }
    }
}
