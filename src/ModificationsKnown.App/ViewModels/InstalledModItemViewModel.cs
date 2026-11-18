using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Nexus;

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
    [NotifyPropertyChangedFor(nameof(UpdateBadgeLabel))]
    private bool _hasUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBadgeLabel))]
    private string _latestVersionLabel = string.Empty;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    public bool IsNexusMatched => NexusMatch is not null;

    public string UpdateBadgeLabel => string.IsNullOrWhiteSpace(LatestVersionLabel)
        ? "可更新"
        : $"可更新 {LatestVersionLabel}";

    public string UpdateMenuLabel => string.IsNullOrWhiteSpace(LatestVersionLabel)
        ? "更新到最新版本…"
        : $"更新到 {LatestVersionLabel}…";

    public bool HasThumbnail => Thumbnail is not null;

    public bool ShowMonogram => Thumbnail is null;

    partial void OnThumbnailChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(ShowMonogram));
    }

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
        Func<InstalledMod, Task> linkNexus,
        Func<InstalledModItemViewModel, Task> update)
    {
        Mod = mod;
        _toggle = toggle;
        ToggleCommand = new AsyncRelayCommand(async () =>
        {
            await toggle(Mod);
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(IsDisabled));
            OnPropertyChanged(nameof(State));
        });
        UninstallCommand = new AsyncRelayCommand(() => uninstall(Mod));
        DeleteWholeCommand = new AsyncRelayCommand(() => deleteWhole(Mod));
        AdoptCommand = new AsyncRelayCommand(() => adopt(Mod));
        LinkNexusCommand = new AsyncRelayCommand(() => linkNexus(Mod));
        UpdateCommand = new AsyncRelayCommand(() => update(this));
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
            if (value == Mod.IsEnabled) return;

            _ = _toggle(Mod);
            // The CheckBox is TwoWay and has already written the clicked value.
            // Re-read the model on the next dispatcher turn so a rejected or
            // busy toggle snaps back instead of lying until page re-entry.
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(IsDisabled));
                OnPropertyChanged(nameof(State));
            });
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

    public IAsyncRelayCommand UpdateCommand { get; }
}
