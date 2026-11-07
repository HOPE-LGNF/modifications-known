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

    public string SourceLabel => NexusMatch is null ? "未关联 Nexus" : "Nexus 模组";

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

    public InstalledModItemViewModel(
        InstalledMod mod,
        Func<InstalledMod, Task> toggle,
        Func<InstalledMod, Task> uninstall,
        Func<InstalledMod, Task> deleteWhole,
        Func<InstalledMod, Task> adopt)
    {
        Mod = mod;
        ToggleCommand = new AsyncRelayCommand(() => toggle(Mod));
        UninstallCommand = new AsyncRelayCommand(() => uninstall(Mod));
        DeleteWholeCommand = new AsyncRelayCommand(() => deleteWhole(Mod));
        AdoptCommand = new AsyncRelayCommand(() => adopt(Mod));
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

    public bool CanDeleteWhole => Mod.CanDeleteWhole;

    public bool CanAdopt => Mod.CanAdopt;

    public bool IsManaged => Mod.IsManaged;

    public string ManagementLabel => Mod.ManagementLabel;

    public string FileStateLabel
    {
        get
        {
            if (!Mod.IsManaged) return "未托管";

            var parts = new List<string>();
            if (Mod.MissingFiles.Count > 0) parts.Add($"缺失 {Mod.MissingFiles.Count}");
            if (Mod.ModifiedFiles.Count > 0) parts.Add($"修改 {Mod.ModifiedFiles.Count}");
            if (Mod.UnexpectedFiles.Count > 0) parts.Add($"新增 {Mod.UnexpectedFiles.Count}");
            return parts.Count == 0 ? "文件完整" : string.Join(" · ", parts);
        }
    }

    public IAsyncRelayCommand ToggleCommand { get; }

    public IAsyncRelayCommand UninstallCommand { get; }

    public IAsyncRelayCommand DeleteWholeCommand { get; }

    public IAsyncRelayCommand AdoptCommand { get; }
}

public partial class InstalledViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly ModOperationService _operations;
    private readonly IUserDialogService _dialogs;
    private readonly IAppSettingsService _settings;
    private readonly NexusMatchService _nexusMatch;
    private readonly IInstalledSourceStore _sources;
    private int _refreshGeneration;

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
        ModOperationService operations,
        IUserDialogService dialogs,
        IAppSettingsService settings,
        NexusMatchService nexusMatch,
        IInstalledSourceStore sources)
    {
        _shell = shell;
        _operations = operations;
        _dialogs = dialogs;
        _settings = settings;
        _nexusMatch = nexusMatch;
        _sources = sources;
        _shell.Game.Changed += (_, _) => _ = RefreshAsync();
    }

    public ObservableCollection<InstalledModItemViewModel> Mods { get; } = [];

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync();

    private async Task RefreshCoreAsync()
    {
        // A scan belongs to the installation selected when it starts. A newer
        // scan supersedes it; the old result must never be painted over the new
        // selection.
        var installation = _shell.Game.Selected;
        var generation = ++_refreshGeneration;
        IsBusy = true;
        try
        {
            if (installation is null)
            {
                if (generation != _refreshGeneration) return;
                Mods.Clear();
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
                if (generation != _refreshGeneration) return;
                Mods.Clear();
                EmptyMessage = "没有找到 BepInEx/plugins。请先安装 BepInEx。";
                CountLabel = "0";
                EnabledLabel = "0";
                return;
            }

            var mods = await _operations.ScanAsync(installation);

            if (generation != _refreshGeneration
                || !ReferenceEquals(_shell.Game.Selected, installation)
                || !PathEquals(_shell.Game.Selected?.GameDirectory, installation.GameDirectory))
                return;

            Mods.Clear();
            foreach (var mod in mods)
                Mods.Add(CreateItem(mod));

            CountLabel = mods.Count.ToString();
            EnabledLabel = mods.Count(mod => mod.IsEnabled).ToString();
            EmptyMessage = mods.Count == 0
                ? "插件目录还是空的。去「模组发现」看看，或手动放入 BepInEx/plugins。"
                : string.Empty;

            _ = MatchNexusAsync(generation);
        }
        finally
        {
            if (generation == _refreshGeneration)
                IsBusy = false;
        }
    }

    private static bool PathEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

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
            var installation = _shell.Game.Selected
                ?? throw new InvalidOperationException("尚未选择游戏安装。");
            var result = await _operations.SetEnabledAsync(installation, mod, !mod.IsEnabled);
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AdoptAsync(InstalledMod mod)
    {
        if (IsBusy) return;
        if (!mod.CanAdopt)
        {
            _shell.SetStatus("这个包不能接管；只有未托管的普通目录可以生成基线清单。", StatusKind.Warning);
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "接管为托管模组",
            $"会把「{mod.Name}」当前目录中的所有文件写入托管清单。\n之后“卸载”会按清单删除未修改文件；用户新增/修改的文件仍会保留。\n\n{mod.DirectoryPath}",
            "接管",
            "取消");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var installation = _shell.Game.Selected
                ?? throw new InvalidOperationException("尚未选择游戏安装。");
            var sidecar = _sources.Get(installation.GameDirectory, mod.PackageId);
            var source = sidecar is null
                ? null
                : PackageSource.Nexus(
                    sidecar.NexusName,
                    sidecar.GameDomain,
                    sidecar.GameScopedId,
                    sidecar.ModUid,
                    sidecar.FileId,
                    sidecar.Version);

            var result = await _operations.AdoptAsync(installation, mod, source);
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UninstallAsync(InstalledMod mod)
    {
        if (IsBusy) return;
        if (!mod.CanDelete)
        {
            _shell.SetStatus("这个包没有安装清单，不能按清单卸载；请使用“删除目录”。", StatusKind.Warning);
            return;
        }

        if (_settings.Current.ConfirmBeforeDelete)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "卸载模组",
                $"将从「{mod.Name}」中移除安装清单记录的文件。\n用户后来新增或修改的文件会保留。\n\n{mod.DirectoryPath}",
                "卸载",
                "取消");
            if (!confirmed) return;
        }

        IsBusy = true;
        try
        {
            var installation = _shell.Game.Selected
                ?? throw new InvalidOperationException("尚未选择游戏安装。");
            var result = await _operations.UninstallManagedAsync(installation, mod);
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteWholeAsync(InstalledMod mod)
    {
        if (IsBusy) return;
        if (!mod.CanDeleteWhole)
        {
            _shell.SetStatus("根目录 DLL 不能删除整个插件目录。", StatusKind.Warning);
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "删除整个模组目录",
            $"这会删除整个目录，包括未托管文件和用户后来添加的文件：\n{mod.DirectoryPath}\n\n此操作不可恢复，确认继续吗？",
            "删除整个目录",
            "取消");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var installation = _shell.Game.Selected
                ?? throw new InvalidOperationException("尚未选择游戏安装。");
            var result = await _operations.DeleteWholePackageAsync(installation, mod);
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
        var sidecar = installation is null ? null : _sources.Get(installation.GameDirectory, mod.PackageId);
        var source = mod.Source;
        var match = source is { Provider: "nexus", GameScopedId: not null }
            ? new NexusMatch(source.NexusName ?? $"Nexus 模组 #{source.GameScopedId}", source.GameScopedId, source.ModUid ?? string.Empty)
            : sidecar is null
                ? null
                : new NexusMatch(sidecar.NexusName, sidecar.GameScopedId, sidecar.ModUid);

        return new InstalledModItemViewModel(mod, ToggleAsync, UninstallAsync, DeleteWholeAsync, AdoptAsync)
        {
            IsDetailed = IsDetailedView,
            NexusDomain = _settings.Current.NexusGameDomain,
            NexusMatch = match
        };
    }

    private async Task MatchNexusAsync(int generation)
    {
        foreach (var item in Mods.ToList())
        {
            if (generation != _refreshGeneration) return;
            if (item.IsNexusMatched) continue;

            var match = await _nexusMatch.ResolveAsync(
                item.Mod.PackageId,
                item.Name,
                item.Mod.Version);
            if (generation != _refreshGeneration) return;
            if (match is not null)
                item.NexusMatch = match;
        }
    }
}
