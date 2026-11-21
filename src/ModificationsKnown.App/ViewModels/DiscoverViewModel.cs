using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Network;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Installation;
using ModificationsKnown.Infrastructure.Storage;
using ModificationsKnown.Infrastructure.Windows;

namespace ModificationsKnown.App.ViewModels;

public partial class ModCardViewModel : ObservableObject
{
    [ObservableProperty]
    private Bitmap? _thumbnail;

    public ModCardViewModel(NexusModSummary summary)
    {
        Summary = summary;
    }

    public NexusModSummary Summary { get; }

    public string Name => Summary.Name;

    public string Author => string.IsNullOrWhiteSpace(Summary.Author) ? "作者未知" : Summary.Author;

    public string SummaryText => string.IsNullOrWhiteSpace(Summary.Summary) ? "暂无简介。" : Summary.Summary;

    public string Version => Summary.VersionLabel;

    public string Downloads => Summary.DownloadsLabel;

    public string Endorsements => Summary.EndorsementsLabel;

    public string FileSize => Summary.FileSizeLabel;

    public string Category => string.IsNullOrWhiteSpace(Summary.Category) ? "未分类" : Summary.Category;

    public string ReleaseDate => Summary.ReleaseDateLabel;

    private static readonly DateTime FullReleaseDate = new(2026, 11, 9);

    /// <summary>Conservative heuristic: a mod last updated before the full
    /// release may only have been built/tested against the demo.</summary>
    public bool MayBeDemoOnly =>
        Summary.UpdatedAt is { } updated && updated.ToLocalTime().Date < FullReleaseDate;

    /// <summary>Author text mention only. This is deliberately *not* treated as
    /// proof of demo-only support: sentences such as “no longer only for the
    /// demo” exist, so the UI says “mentioned”, never “confirmed”.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDemoHint))]
    [NotifyPropertyChangedFor(nameof(DemoHintLabel))]
    [NotifyPropertyChangedFor(nameof(DemoHintTooltip))]
    private bool _descriptionMentionsDemo;

    public bool MentionsDemo =>
        ContainsDemoMention(Summary.Summary) || DescriptionMentionsDemo;

    public static bool ContainsDemoMention(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && System.Text.RegularExpressions.Regex.IsMatch(
            text,
            @"\b(demo|试玩|演示版|demo\s*版)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public bool ShowDemoHint => MayBeDemoOnly || MentionsDemo;

    public string DemoHintLabel =>
        MayBeDemoOnly && MentionsDemo ? "Demo 提示"
        : MayBeDemoOnly ? "可能 Demo"
        : "作者提到 Demo";

    public string DemoHintTooltip =>
        MayBeDemoOnly && MentionsDemo
            ? "最近更新在正式版发布（2026-11-09）之前，且作者说明提到 Demo。这仍不是“仅支持 Demo”的证明，请阅读原文确认。"
            : MayBeDemoOnly
                ? "最近更新在正式版发布（2026-11-09）之前，可能仅适配 Demo；请查看作者说明。"
                : "作者说明提到 Demo，但不代表明确仅支持 Demo；请阅读原文确认。";
}

public partial class DiscoverViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly INexusModsClient _nexus;
    private readonly IAppSettingsService _settings;
    private readonly IFileDownloader _downloader;
    private readonly ModOperationService _operations;
    private readonly ThumbnailService _thumbnails;
    private readonly IInstalledSourceStore _sources;
    private readonly NxmProtocolRegistrar _nxmRegistrar;
    private readonly IUserDialogService _dialogs;
    private readonly PackageUpdateService _updates;
    private readonly ModUpdateResolver _updateResolver;
    private readonly IAppLogger _logger;

    private bool _suppressFileSelection;
    private int _loadVersion;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _sortIndex;

    [ObservableProperty]
    private ModCardViewModel? _selectedCard;

    [ObservableProperty]
    private NexusModDetails? _details;

    [ObservableProperty]
    private Bitmap? _detailImage;

    [ObservableProperty]
    private NexusModFile? _selectedFile;

    [ObservableProperty]
    private NexusFileVersion? _selectedVersion;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isDetailLoading;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _apiKeyMissing;

    [ObservableProperty]
    private string _selectedCategory = AllCategories;

    [ObservableProperty]
    private string _resultSummary = "输入关键词，或直接浏览 Nexus 上的 CU 模组。";

    [ObservableProperty]
    private string _downloadStatus = string.Empty;

    [ObservableProperty]
    private bool _showFreeDownloadWhy;

    [ObservableProperty]
    private bool _hasNxmFallback;

    [ObservableProperty]
    private bool _nxmRegistered;

    [ObservableProperty]
    private bool _isDescriptionExpanded;

    [ObservableProperty]
    private bool _hasLongDescription;

    public double DescriptionMaxHeight => IsDescriptionExpanded ? 5000 : 150;

    public string DescriptionToggleText => IsDescriptionExpanded ? "收起描述" : "展开全部";

    partial void OnIsDescriptionExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(DescriptionMaxHeight));
        OnPropertyChanged(nameof(DescriptionToggleText));
    }

    [RelayCommand]
    private void ToggleDescription()
    {
        IsDescriptionExpanded = !IsDescriptionExpanded;
    }

    public bool NxmNotRegistered => !NxmRegistered;

    [RelayCommand]
    private async Task RegisterNxmAsync()
    {
        await NxmRegistrationFlow.RegisterAsync(_dialogs, _nxmRegistrar, _shell);
        RefreshNxmStatus();
    }

    partial void OnNxmRegisteredChanged(bool value) => OnPropertyChanged(nameof(NxmNotRegistered));

    [ObservableProperty]
    private string _nxmRegistrationHint = "尚未注册 NXM 链接。";

    private string? _nxmFallbackUrl;
    private PendingFreeDownload? _pendingFreeDownload;
    private bool _openNexusWithNxm = true;
    private readonly List<ModCardViewModel> _allCards = [];

    public const string AllCategories = "全部分类";

    public DiscoverViewModel(
        IAppShell shell,
        INexusModsClient nexus,
        IAppSettingsService settings,
        IFileDownloader downloader,
        ModOperationService operations,
        ThumbnailService thumbnails,
        IInstalledSourceStore sources,
        NxmProtocolRegistrar nxmRegistrar,
        IUserDialogService dialogs,
        PackageUpdateService updates,
        ModUpdateResolver updateResolver,
        IAppLogger logger)
    {
        _shell = shell;
        _nexus = nexus;
        _settings = settings;
        _downloader = downloader;
        _operations = operations;
        _thumbnails = thumbnails;
        _sources = sources;
        _nxmRegistrar = nxmRegistrar;
        _dialogs = dialogs;
        _updates = updates;
        _updateResolver = updateResolver;
        _logger = logger;
        Mods.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowListPlaceholder));
        ApiKeyMissing = string.IsNullOrWhiteSpace(settings.Current.NexusApiKey);
        _shell.Game.Changed += (_, _) => OnPropertyChanged(nameof(CanInstall));
    }

    public ObservableCollection<ModCardViewModel> Mods { get; } = [];

    public ObservableCollection<string> Categories { get; } = [AllCategories];

    public ObservableCollection<NexusModFile> Files { get; } = [];

    public ObservableCollection<NexusFileVersion> Versions { get; } = [];

    public bool HasSelection => SelectedCard is not null;

    public bool ShowListPlaceholder => IsLoading && Mods.Count == 0;

    partial void OnIsLoadingChanged(bool value) =>
        OnPropertyChanged(nameof(ShowListPlaceholder));

    public bool HasNoSelection => SelectedCard is null;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey);

    public bool CanInstall => _shell.Game.Selected?.HasBepInEx == true;

    public string PageLabel => $"第 {CurrentPage} / {Math.Max(1, TotalPages)} 页";

    public string InstallHint => CanInstall
        ? "下载后将直接安装到当前游戏的 BepInEx/plugins。"
        : "未检测到 BepInEx；仍可下载，但安装会失败。";

    public string NexusPageUrl => SelectedCard is null
        ? string.Empty
        : $"https://www.nexusmods.com/{_settings.Current.NexusGameDomain}/mods/{SelectedCard.Summary.GameScopedId}";

    public Task OnActivatedAsync()
    {
        ApiKeyMissing = !HasApiKey;
        RefreshNxmStatus();
        return Mods.Count > 0 ? Task.CompletedTask : LoadPageAsync();
    }

    partial void OnSelectedCategoryChanged(string value) => ApplyCategoryFilter();

    [RelayCommand]
    private async Task SearchAsync()
    {
        CurrentPage = 1;
        await LoadPageAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadPageAsync();

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        await LoadPageAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPage >= TotalPages) return;
        CurrentPage++;
        await LoadPageAsync();
    }

    [RelayCommand]
    private async Task SelectModAsync(ModCardViewModel? card)
    {
        if (card is null) return;
        if (ReferenceEquals(card, SelectedCard) && Details is not null) return;

        SelectedCard = card;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(NexusPageUrl));
        Details = null;
        DetailImage = null;
        IsDescriptionExpanded = false;
        HasLongDescription = false;
        Files.Clear();
        Versions.Clear();
        SelectedFile = null;
        SelectedVersion = null;
        ShowFreeDownloadWhy = false;

        IsDetailLoading = true;
        var version = ++_loadVersion;
        try
        {
            var details = await _nexus.GetModAsync(
                _settings.Current.NexusGameDomain,
                card.Summary.GameScopedId);
            if (version != _loadVersion) return;

            Details = details;
            card.DescriptionMentionsDemo = ModCardViewModel.ContainsDemoMention(details.DescriptionText);
            HasLongDescription = details.DescriptionText.Length > 240;
            IsDescriptionExpanded = false;
            var imageUrl = details.GalleryUrls.FirstOrDefault() ?? card.Summary.ThumbnailUrl;
            DetailImage = await _thumbnails.LoadAsync(imageUrl);

            var files = await _nexus.GetFilesAsync(card.Summary.Id);
            if (version != _loadVersion) return;

            foreach (var file in files.OrderByDescending(file => file.IsActive)) Files.Add(file);
            var preferred = Files.FirstOrDefault(file => file.IsActive) ?? Files.FirstOrDefault();
            if (preferred is not null)
            {
                _suppressFileSelection = true;
                SelectedFile = preferred;
                _suppressFileSelection = false;
                await LoadVersionsAsync(preferred, version);
            }
        }
        catch (NexusApiException exception)
        {
            _shell.SetStatus(exception.Message, StatusKind.Error);
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"读取详情失败：{exception.Message}", StatusKind.Error);
        }
        finally
        {
            if (version == _loadVersion) IsDetailLoading = false;
        }
    }

    [RelayCommand]
    private void OpenNxmDownloadInBrowser()
    {
        if (string.IsNullOrWhiteSpace(_nxmFallbackUrl)) return;

        try
        {
            Process.Start(new ProcessStartInfo(_nxmFallbackUrl) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开下载直链失败：{exception.Message}", StatusKind.Error);
        }
    }

    [RelayCommand]
    private void OpenSettings() => _shell.NavigateTo("settings");

    [RelayCommand]
    private void OpenNexus()
    {
        if (string.IsNullOrWhiteSpace(NexusPageUrl)) return;

        try
        {
            Process.Start(new ProcessStartInfo(NexusPageUrl) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开浏览器失败：{exception.Message}", StatusKind.Error);
        }
    }

    [RelayCommand]
    private Task SelectFileAsync(NexusModFile? file)
    {
        if (file is null || _suppressFileSelection) return Task.CompletedTask;
        SelectedFile = file;
        return Task.CompletedTask;
    }

    public string SelectedFileLabel => SelectedFile?.Name ?? "尚未选择文件";

    public string SelectedVersionLabel => SelectedVersion is null
        ? "尚未选择版本"
        : $"{NexusVersionText.Label(SelectedVersion, SelectedFile)} · {SelectedVersion.CategoryLabel} · {SelectedVersion.UploadedLabel}";

    partial void OnSelectedFileChanged(NexusModFile? value)
    {
        OnPropertyChanged(nameof(SelectedFileLabel));
        if (value is null || _suppressFileSelection) return;
        _ = LoadVersionsAsync(value, ++_loadVersion);
    }

    partial void OnSelectedVersionChanged(NexusFileVersion? value)
    {
        OnPropertyChanged(nameof(SelectedVersionLabel));
    }

    partial void OnSortIndexChanged(int value)
    {
        CurrentPage = 1;
        _ = LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        if (IsLoading) return;

        ApiKeyMissing = !HasApiKey;
        IsLoading = true;
        var page = CurrentPage;
        try
        {
            var pageSize = Math.Clamp(_settings.Current.PageSize, 1, 100);

            var query = new NexusSearchQuery(
                SearchText,
                page,
                pageSize,
                SortIndex switch
                {
                    1 => NexusModSort.Downloads,
                    2 => NexusModSort.New,
                    3 => NexusModSort.Updated,
                    _ => NexusModSort.Endorsements
                },
                _settings.Current.IncludeAdultMods);

            var result = await _nexus.SearchModsAsync(_settings.Current.NexusGameDomain, query);
            _allCards.Clear();
            foreach (var summary in result.Items)
            {
                var card = new ModCardViewModel(summary);
                _allCards.Add(card);
                _ = LoadCardThumbnailAsync(card);
            }

            var categories = _allCards
                .Select(card => card.Summary.Category)
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Select(category => category!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Keep the "all" entry in place instead of clearing: clearing leaves
            // the ComboBox without a selection while SelectedCategory is set.
            var previous = SelectedCategory;
            while (Categories.Count > 1) Categories.RemoveAt(Categories.Count - 1);
            foreach (var category in categories) Categories.Add(category);
            var next = Categories.Contains(previous) ? previous : AllCategories;
            if (SelectedCategory == next) ApplyCategoryFilter();
            else SelectedCategory = next;

            TotalCount = result.TotalCount;
            TotalPages = result.TotalPages;
            CurrentPage = result.Page;
            ResultSummary = string.IsNullOrWhiteSpace(SearchText)
                ? $"共 {TotalCount} 个模组 · 第 {CurrentPage} 页"
                : $"找到 {TotalCount} 个匹配模组 · 第 {CurrentPage} 页";
            OnPropertyChanged(nameof(PageLabel));
        }
        catch (NexusApiException exception)
        {
            _shell.SetStatus(exception.Message, StatusKind.Error);
            ResultSummary = exception.Message;
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"加载模组失败：{exception.Message}", StatusKind.Error);
            ResultSummary = "加载失败，请稍后重试。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyCategoryFilter()
    {
        var filtered = SelectedCategory == AllCategories
            ? _allCards
            : _allCards.Where(card =>
                string.Equals(card.Summary.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase)).ToList();

        var previous = SelectedCard;
        Mods.Clear();
        foreach (var card in filtered) Mods.Add(card);

        // Selection is intentionally sticky: changing a category, searching or
        // paging must not discard the mod the user is reading. Only selecting
        // another card replaces it. The ListBox is OneWay-bound, so clearing
        // its items cannot null SelectedCard behind our back.
        if (previous is not null && filtered.Contains(previous))
            OnPropertyChanged(nameof(SelectedCard));
    }

    private async Task LoadCardThumbnailAsync(ModCardViewModel card)
    {
        card.Thumbnail = await _thumbnails.LoadAsync(card.Summary.ThumbnailUrl);
    }

    private async Task LoadVersionsAsync(NexusModFile file, int version)
    {
        Versions.Clear();
        SelectedVersion = null;
        OnPropertyChanged(nameof(InstallHint));
        try
        {
            var versions = await _nexus.GetFileVersionsAsync(file.Id);
            if (version != _loadVersion) return;

            foreach (var item in versions.OrderByDescending(item => item.IsPrimary)
                         .ThenByDescending(item => item.UploadedAt))
                Versions.Add(item);

            SelectedVersion = Versions.FirstOrDefault(item => item.IsPrimary)
                              ?? Versions.FirstOrDefault(item => item.Category is "main" or "update")
                              ?? Versions.FirstOrDefault();
        }
        catch (NexusApiException exception)
        {
            _shell.SetStatus(exception.Message, StatusKind.Error);
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"读取文件版本失败：{exception.Message}", StatusKind.Error);
        }
    }

    private sealed record PendingFreeDownload(
        GameInstallation Installation,
        string GameDomain,
        long ModId,
        long FileId,
        string NexusName,
        string? Version,
        string ModUid,
        string? FileIdString);

    private static string GetExtension(string url)
    {
        var extension = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        return extension is ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2"
            ? extension
            : ".zip";
    }
}
