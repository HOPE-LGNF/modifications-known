using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.App.ViewModels;

public sealed partial class InstalledModItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isDetailed = true;

    [ObservableProperty]
    private bool _isCompact;

    [ObservableProperty]
    private NexusMatch? _nexusMatch;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    public bool IsNexusMatched => NexusMatch is not null;

    public bool ShowMonogram => Thumbnail is null;

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(ShowMonogram));

    public bool IsDetailedMode => !IsCompact;

    partial void OnIsCompactChanged(bool value) => OnPropertyChanged(nameof(IsDetailedMode));

    public string Monogram
    {
        get
        {
            var name = Name.Trim();
            if (name.Length == 0) return "?";
            return name.Length == 1 ? name.ToUpperInvariant() : name[..2].ToUpperInvariant();
        }
    }

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

    private readonly Func<InstalledMod, Task> _toggle;

    public InstalledModItemViewModel(
        InstalledMod mod,
        Func<InstalledMod, Task> toggle,
        Func<InstalledMod, Task> uninstall,
        Func<InstalledMod, Task> deleteWhole,
        Func<InstalledMod, Task> adopt,
        Func<InstalledMod, Task> linkNexus)
    {
        Mod = mod;
        _toggle = toggle;
        ToggleCommand = new AsyncRelayCommand(() => toggle(Mod));
        UninstallCommand = new AsyncRelayCommand(() => uninstall(Mod));
        DeleteWholeCommand = new AsyncRelayCommand(() => deleteWhole(Mod));
        AdoptCommand = new AsyncRelayCommand(() => adopt(Mod));
        LinkNexusCommand = new AsyncRelayCommand(() => linkNexus(Mod));
    }

    public InstalledMod Mod { get; }

    public string Name => Mod.Name;

    public string Version => Mod.VersionLabel;

    public string State => Mod.StateLabel;

    public bool IsEnabled
    {
        get => Mod.IsEnabled;
        set
        {
            if (value != Mod.IsEnabled)
                _ = _toggle(Mod);
        }
    }

    public bool IsDisabled => !Mod.IsEnabled;

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

    public string FileStateTooltip => !Mod.IsManaged
        ? "未托管：没有安装清单，不进行文件完整性比对。"
        : "缺失：清单记录了但磁盘上找不到；修改：大小或 SHA-256 与清单不符；新增：磁盘上有但清单没有。禁用时 DLL 会改名为 .dll.disabled，这不算缺失或新增。";

    public IAsyncRelayCommand ToggleCommand { get; }

    public IAsyncRelayCommand UninstallCommand { get; }

    public IAsyncRelayCommand DeleteWholeCommand { get; }

    public IAsyncRelayCommand AdoptCommand { get; }

    public IAsyncRelayCommand LinkNexusCommand { get; }
}

public partial class InstalledViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly ModOperationService _operations;
    private readonly IUserDialogService _dialogs;
    private readonly IAppSettingsService _settings;
    private readonly NexusMatchService _nexusMatch;
    private readonly IInstalledSourceStore _sources;
    private readonly IFilePickerService _filePicker;
    private readonly NexusManualMatchStore _manualMatches;
    private readonly ModGroupStore _groupStore;
    private readonly INexusModsClient _nexus;
    private readonly ThumbnailService _thumbnails;
    private readonly List<InstalledModItemViewModel> _allItems = [];
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

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ModGroup? _selectedGroup;

    [ObservableProperty]
    private bool _isModified;

    public bool CanDeleteGroup => SelectedGroup is { IsDefault: false };

    public bool HasSelectedGroup => SelectedGroup is not null;

    /// <summary>True while the group list is being rebuilt; the view must not
    /// treat the resulting ComboBox churn as a user-initiated switch.</summary>
    public bool IsReloadingGroups { get; private set; }

    public string SaveButtonText => IsModified ? "保存修改 *" : "保存当前状态";

    public string GroupHint => IsModified
        ? "有未保存的组修改"
        : "启用/禁用立即生效；保存只更新组快照";

    partial void OnIsModifiedChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(GroupHint));
    }

    partial void OnSelectedGroupChanged(ModGroup? value)
    {
        OnPropertyChanged(nameof(CanDeleteGroup));
        OnPropertyChanged(nameof(HasSelectedGroup));
    }

    private void MarkModifiedIfNeeded()
    {
        if (SelectedGroup is null) return;
        IsModified = true;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public bool IsDetailedView => !IsCompactView;

    public string ViewModeButtonText => IsCompactView ? "详细列表" : "紧凑列表";

    partial void OnIsCompactViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDetailedView));
        OnPropertyChanged(nameof(ViewModeButtonText));

        foreach (var item in _allItems)
        {
            item.IsDetailed = !value;
            item.IsCompact = value;
        }
    }

    public InstalledViewModel(
        IAppShell shell,
        ModOperationService operations,
        IUserDialogService dialogs,
        IAppSettingsService settings,
        NexusMatchService nexusMatch,
        IInstalledSourceStore sources,
        IFilePickerService filePicker,
        NexusManualMatchStore manualMatches,
        ModGroupStore groupStore,
        INexusModsClient nexus,
        ThumbnailService thumbnails)
    {
        _shell = shell;
        _operations = operations;
        _dialogs = dialogs;
        _settings = settings;
        _nexusMatch = nexusMatch;
        _sources = sources;
        _filePicker = filePicker;
        _manualMatches = manualMatches;
        _groupStore = groupStore;
        _nexus = nexus;
        _thumbnails = thumbnails;
        _shell.Game.Changed += (_, _) => _ = RefreshAsync();
    }

    public ObservableCollection<InstalledModItemViewModel> Mods { get; } = [];

    public ObservableCollection<ModGroup> Groups { get; } = [];

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
                _allItems.Clear();
                ApplyFilter();
                Groups.Clear();
                SelectedGroup = null;
                IsModified = false;
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
                _allItems.Clear();
                ApplyFilter();
                EmptyMessage = "没有找到 BepInEx/plugins。请先安装 BepInEx。";
                Groups.Clear();
                SelectedGroup = null;
                IsModified = false;
                CountLabel = "0";
                EnabledLabel = "0";
                return;
            }

            var mods = await _operations.ScanAsync(installation);

            if (generation != _refreshGeneration
                || !ReferenceEquals(_shell.Game.Selected, installation)
                || !PathEquals(_shell.Game.Selected?.GameDirectory, installation.GameDirectory))
                return;

            _allItems.Clear();
            foreach (var mod in mods)
                _allItems.Add(CreateItem(mod));

            ApplyFilter();
            LoadGroups(installation);
            CountLabel = mods.Count.ToString();
            EnabledLabel = mods.Count(mod => mod.IsEnabled).ToString();
            EmptyMessage = mods.Count == 0
                ? "插件目录还是空的。去「模组发现」看看，或手动放入 BepInEx/plugins。"
                : string.Empty;

            _ = MatchAndLoadThumbnailsAsync(generation);
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
            var action = mod.IsEnabled ? "禁用" : "启用";
            var result = await _operations.SetEnabledAsync(installation, mod, !mod.IsEnabled);
            if (result.Success) MarkModifiedIfNeeded();
            _shell.SetStatus(
                result.Success ? $"已{action}「{mod.Name}」。" : $"{action}「{mod.Name}」失败：{result.Message}",
                result.Success ? StatusKind.Success : StatusKind.Error);
            await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LinkNexusAsync(InstalledMod mod)
    {
        if (IsBusy) return;
        var installation = _shell.Game.Selected;
        if (installation is null) return;

        var match = await _dialogs.PickNexusMatchAsync(_settings.Current.NexusGameDomain, mod.Name);
        if (match is null) return;

        _manualMatches.Set(installation.GameDirectory, mod.PackageId, match);
        var item = _allItems.FirstOrDefault(candidate =>
            string.Equals(candidate.Mod.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase)
            && PathEquals(candidate.Mod.DirectoryPath, mod.DirectoryPath));
        if (item is not null)
        {
            item.NexusDomain = _settings.Current.NexusGameDomain;
            item.NexusMatch = match;
        }

        _shell.SetStatus($"已关联「{mod.Name}」→ {match.Name}。", StatusKind.Success);
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
        var manual = installation is null ? null : _manualMatches.Get(installation.GameDirectory, mod.PackageId);
        var source = mod.Source;
        var match = manual
            ?? (source is { Provider: "nexus", GameScopedId: not null }
                ? new NexusMatch(source.NexusName ?? $"Nexus 模组 #{source.GameScopedId}", source.GameScopedId, source.ModUid ?? string.Empty)
                : sidecar is null
                    ? null
                    : new NexusMatch(sidecar.NexusName, sidecar.GameScopedId, sidecar.ModUid));

        return new InstalledModItemViewModel(mod, ToggleAsync, UninstallAsync, DeleteWholeAsync, AdoptAsync, LinkNexusAsync)
        {
            IsDetailed = IsDetailedView,
            IsCompact = IsCompactView,
            NexusDomain = _settings.Current.NexusGameDomain,
            NexusMatch = match
        };
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allItems
            : _allItems.Where(item =>
                    item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.PackagePath.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        Mods.Clear();
        foreach (var item in filtered)
            Mods.Add(item);

        if (_allItems.Count > 0 && filtered.Count == 0)
            EmptyMessage = "没有匹配当前搜索的模组。";
        else if (_allItems.Count > 0)
            EmptyMessage = string.Empty;
    }

    [RelayCommand]
    private async Task InstallLocalArchiveAsync()
    {
        if (IsBusy || !EnsureInstallTarget(out _)) return;

        var path = await _filePicker.PickArchiveAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        await InstallArchivesAsync([path]);
    }

    public async Task InstallArchivesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || paths.Count == 0 || !EnsureInstallTarget(out var installation)) return;

        IsBusy = true;
        try
        {
            var failures = new List<string>();
            ModInstallResult? last = null;
            foreach (var path in paths)
            {
                last = await _operations.InstallArchiveAsync(installation, path);
                if (!last.Success) failures.Add($"{Path.GetFileName(path)}：{last.Message}");
            }

            if (paths.Count == 1)
                _shell.SetStatus(last!.Message, last.Success ? StatusKind.Success : StatusKind.Error);
            else if (failures.Count == 0)
                _shell.SetStatus($"已安装 {paths.Count} 个压缩包。", StatusKind.Success);
            else
                _shell.SetStatus(
                    $"{paths.Count - failures.Count}/{paths.Count} 个压缩包安装成功。失败：{string.Join("；", failures)}",
                    StatusKind.Error);

            await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool EnsureInstallTarget([NotNullWhen(true)] out GameInstallation? installation)
    {
        installation = _shell.Game.Selected;
        if (installation is { HasPluginsDirectory: true }) return true;

        _shell.SetStatus("请先选择已安装 BepInEx 的游戏。", StatusKind.Warning);
        return false;
    }

    [RelayCommand]
    private async Task EnableAllAsync()
    {
        await SetAllEnabledAsync(true);
    }

    [RelayCommand]
    private async Task DisableAllAsync()
    {
        await SetAllEnabledAsync(false);
    }

    private async Task SetAllEnabledAsync(bool enabled)
    {
        if (IsBusy) return;
        var installation = _shell.Game.Selected;
        if (installation is null) return;

        IsBusy = true;
        try
        {
            var failures = new List<string>();
            var changed = false;
            foreach (var item in _allItems.ToList())
            {
                if (item.Mod.IsEnabled == enabled) continue;
                var result = await _operations.SetEnabledAsync(installation, item.Mod, enabled);
                if (result.Success) changed = true;
                else failures.Add($"{item.Name}: {result.Message}");
            }

            if (changed) MarkModifiedIfNeeded();
            _shell.SetStatus(
                failures.Count == 0
                    ? (enabled ? "已启用全部模组。" : "已禁用全部模组。")
                    : $"部分操作失败：{string.Join("；", failures)}",
                failures.Count == 0 ? StatusKind.Success : StatusKind.Warning);
            await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadGroups(GameInstallation installation)
    {
        var previousId = SelectedGroup?.Id;
        var groups = _groupStore.Get(installation.GameDirectory).ToList();
        if (groups.All(group => !group.IsDefault))
        {
            var defaultGroup = new ModGroup(
                Guid.NewGuid().ToString("N"),
                "默认组",
                SnapshotEnabledPackageIds(),
                DateTimeOffset.UtcNow,
                IsDefault: true);
            _groupStore.Add(installation.GameDirectory, defaultGroup);
            groups.Add(defaultGroup);
        }

        IsReloadingGroups = true;
        try
        {
            Groups.Clear();
            foreach (var group in groups.OrderByDescending(group => group.IsDefault)
                         .ThenBy(group => group.CreatedAt))
                Groups.Add(group);

            var next = Groups.FirstOrDefault(group => group.Id == previousId)
                       ?? Groups.FirstOrDefault(group => group.IsDefault)
                       ?? Groups.FirstOrDefault();

            // Refresh keeps the current group; only an actual group change
            // clears the unsaved marker, otherwise a toggle would be wiped by
            // the rescan.
            var switching = !string.Equals(previousId, next?.Id, StringComparison.Ordinal);
            if (!switching)
            {
                // Rebuilding ItemsSource clears the ComboBox's local selection;
                // the one-way binding only refreshes when the value changes, so
                // nudge it even when the same group comes back.
                SelectedGroup = null;
            }

            SelectedGroup = next;
            if (switching) IsModified = false;
        }
        finally
        {
            IsReloadingGroups = false;
        }
    }

    private List<string> SnapshotEnabledPackageIds() =>
        _allItems
            .Where(item => item.Mod.IsEnabled)
            .Select(item => item.Mod.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Called by the view when the group ComboBox changes. When the current
    /// group has unsaved edits the switch is confirmed first; a false result
    /// tells the view to restore the previous selection.
    /// </summary>
    public async Task<bool> TrySelectGroupAsync(ModGroup group)
    {
        if (SelectedGroup?.Id == group.Id) return true;

        if (IsModified)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "切换 Mod 组",
                $"「{SelectedGroup?.Name ?? "当前组"}」有尚未保存到组快照的启用状态修改。\n\n" +
                "切换会丢弃这些修改。磁盘上的启用状态保持不变，但不会写回任何组快照。要继续吗？",
                "丢弃并切换",
                "取消");
            if (!confirmed) return false;
        }

        SelectedGroup = Groups.FirstOrDefault(candidate => candidate.Id == group.Id) ?? group;
        IsModified = false;
        _shell.SetStatus(
            $"已切换到 Mod 组「{SelectedGroup.Name}」；点“应用该组”才会改写启用状态。",
            StatusKind.Info);
        return true;
    }

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        var installation = _shell.Game.Selected;
        if (installation is null) return;

        var input = await _dialogs.PromptAsync(
            "新建 Mod 组",
            "新组会保存当前版本的启用状态快照。",
            $"组 {Groups.Count + 1}",
            "Mod 组名称");
        if (input is null) return;

        var name = input.Trim();
        if (name.Length == 0)
        {
            _shell.SetStatus("Mod 组名称不能为空。", StatusKind.Warning);
            return;
        }

        if (Groups.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _shell.SetStatus($"已存在同名 Mod 组「{name}」；请先重命名或选择它。", StatusKind.Warning);
            return;
        }

        var group = new ModGroup(
            Guid.NewGuid().ToString("N"),
            name,
            SnapshotEnabledPackageIds(),
            DateTimeOffset.UtcNow);
        _groupStore.Add(installation.GameDirectory, group);
        LoadGroups(installation);
        SelectedGroup = Groups.FirstOrDefault(candidate => candidate.Id == group.Id) ?? group;
        IsModified = false;
        _shell.SetStatus($"已新建 Mod 组「{name}」。", StatusKind.Success);
    }

    [RelayCommand]
    private async Task RenameGroupAsync()
    {
        if (SelectedGroup is not { } group) return;
        if (group.IsDefault)
        {
            _shell.SetStatus("默认组名称固定；可以修改默认组内的启用集合。", StatusKind.Warning);
            return;
        }

        var installation = _shell.Game.Selected;
        if (installation is null) return;

        var input = await _dialogs.PromptAsync(
            "重命名 Mod 组",
            "只修改组名，不影响组内保存的启用状态。",
            group.Name,
            "Mod 组名称");
        if (input is null) return;

        var name = input.Trim();
        if (name.Length == 0)
        {
            _shell.SetStatus("Mod 组名称不能为空。", StatusKind.Warning);
            return;
        }

        if (string.Equals(name, group.Name, StringComparison.Ordinal))
        {
            _shell.SetStatus("组名没有变化。", StatusKind.Info);
            return;
        }

        if (Groups.Any(candidate => candidate.Id != group.Id
                                    && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _shell.SetStatus($"已存在同名 Mod 组「{name}」。", StatusKind.Warning);
            return;
        }

        var updated = group with { Name = name };
        _groupStore.Update(installation.GameDirectory, updated);
        LoadGroups(installation);
        SelectedGroup = Groups.FirstOrDefault(candidate => candidate.Id == group.Id) ?? updated;
        _shell.SetStatus($"已重命名为「{name}」。", StatusKind.Success);
    }

    [RelayCommand]
    private async Task SaveCurrentGroupAsync()
    {
        if (SelectedGroup is not { } group) return;
        var installation = _shell.Game.Selected;
        if (installation is null) return;

        var updated = group with { EnabledPackageIds = SnapshotEnabledPackageIds() };
        _groupStore.Update(installation.GameDirectory, updated);
        LoadGroups(installation);
        SelectedGroup = Groups.FirstOrDefault(candidate => candidate.Id == group.Id) ?? updated;
        IsModified = false;
        _shell.SetStatus($"已把当前启用集合保存到「{group.Name}」。", StatusKind.Success);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ApplyGroupAsync()
    {
        if (IsBusy || SelectedGroup is not { } group) return;
        var installation = _shell.Game.Selected;
        if (installation is null) return;

        var confirmed = await _dialogs.ConfirmAsync(
            "应用 Mod 组",
            $"将把启用状态切换为「{group.Name}」的快照。\n\n组内启用 {group.EnabledPackageIds.Count} 个包，其余包会被禁用。",
            "应用",
            "取消");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var enabledIds = group.EnabledPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();
            foreach (var item in _allItems.ToList())
            {
                var desired = enabledIds.Contains(item.Mod.PackageId);
                if (item.Mod.IsEnabled == desired) continue;
                var result = await _operations.SetEnabledAsync(installation, item.Mod, desired);
                if (!result.Success) failures.Add($"{item.Name}: {result.Message}");
            }

            _shell.SetStatus(
                failures.Count == 0
                    ? $"已应用 Mod 组「{group.Name}」。"
                    : $"Mod 组应用部分失败：{string.Join("；", failures)}",
                failures.Count == 0 ? StatusKind.Success : StatusKind.Warning);
            await RefreshCoreAsync();
            if (failures.Count == 0) IsModified = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (SelectedGroup is not { } group) return;
        if (group.IsDefault)
        {
            _shell.SetStatus("默认组不能删除。", StatusKind.Warning);
            return;
        }

        var installation = _shell.Game.Selected;
        if (installation is null) return;

        var confirmed = await _dialogs.ConfirmAsync(
            "删除 Mod 组",
            $"将删除组快照「{group.Name}」。\n\n这不会改变当前启用状态，也不会删除任何模组文件。",
            "删除组",
            "取消");
        if (!confirmed) return;

        _groupStore.Remove(installation.GameDirectory, group.Id);
        LoadGroups(installation);
        IsModified = false;
        _shell.SetStatus($"已删除 Mod 组「{group.Name}」。", StatusKind.Success);
    }

    private async Task MatchAndLoadThumbnailsAsync(int generation)
    {
        await MatchNexusAsync(generation);
        if (generation != _refreshGeneration) return;

        foreach (var item in _allItems.ToList())
        {
            if (generation != _refreshGeneration) return;
            if (item.NexusMatch is null || item.Thumbnail is not null) continue;

            try
            {
                var details = await _nexus.GetModAsync(item.NexusDomain, item.NexusMatch.GameScopedId);
                var url = details.Summary.ThumbnailUrl ?? details.GalleryUrls.FirstOrDefault();
                var bitmap = await _thumbnails.LoadAsync(url);
                if (bitmap is not null && generation == _refreshGeneration)
                    item.Thumbnail = bitmap;
            }
            catch
            {
                // Thumbnails are cosmetic; Nexus failures must not break the list.
            }
        }
    }

    private async Task MatchNexusAsync(int generation)
    {
        foreach (var item in _allItems.ToList())
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
