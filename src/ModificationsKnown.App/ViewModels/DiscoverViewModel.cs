using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Network;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;
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
    private bool _hasNxmFallback;

    [ObservableProperty]
    private bool _nxmRegistered;

    [ObservableProperty]
    private bool _isDescriptionExpanded;

    [ObservableProperty]
    private bool _isImageExpanded;

    public double DetailImageHeight => IsImageExpanded ? 480 : 210;

    partial void OnIsImageExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(DetailImageHeight));
    }

    [RelayCommand]
    private void ToggleImage()
    {
        IsImageExpanded = !IsImageExpanded;
    }

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

    partial void OnNxmRegisteredChanged(bool value) => OnPropertyChanged(nameof(NxmNotRegistered));

    [ObservableProperty]
    private string _nxmRegistrationHint = "尚未注册 NXM 链接。";

    private string? _nxmFallbackUrl;
    private PendingFreeDownload? _pendingFreeDownload;
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
        NxmProtocolRegistrar nxmRegistrar)
    {
        _shell = shell;
        _nexus = nexus;
        _settings = settings;
        _downloader = downloader;
        _operations = operations;
        _thumbnails = thumbnails;
        _sources = sources;
        _nxmRegistrar = nxmRegistrar;
        ApiKeyMissing = string.IsNullOrWhiteSpace(settings.Current.NexusApiKey);
        _shell.Game.Changed += (_, _) => OnPropertyChanged(nameof(CanInstall));
    }

    public ObservableCollection<ModCardViewModel> Mods { get; } = [];

    public ObservableCollection<string> Categories { get; } = [];

    public ObservableCollection<NexusModFile> Files { get; } = [];

    public ObservableCollection<NexusFileVersion> Versions { get; } = [];

    public bool HasSelection => SelectedCard is not null;

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

        SelectedCard = card;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(NexusPageUrl));
        Details = null;
        DetailImage = null;
        IsDescriptionExpanded = false;
        HasLongDescription = false;
        IsImageExpanded = false;
        Files.Clear();
        Versions.Clear();
        SelectedFile = null;
        SelectedVersion = null;

        IsDetailLoading = true;
        var version = ++_loadVersion;
        try
        {
            var details = await _nexus.GetModAsync(
                _settings.Current.NexusGameDomain,
                card.Summary.GameScopedId);
            if (version != _loadVersion) return;

            Details = details;
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
    private async Task DownloadAsync()
    {
        if (SelectedCard is null || SelectedVersion is null)
        {
            _shell.SetStatus("请先选择一个文件版本。", StatusKind.Warning);
            return;
        }

        if (_shell.Game.Selected is not { } installation)
        {
            _shell.SetStatus("请先选择游戏安装。", StatusKind.Warning);
            return;
        }

        if (!installation.HasBepInEx)
        {
            _shell.SetStatus("当前游戏没有安装 BepInEx，无法安装模组。", StatusKind.Warning);
            return;
        }

        // Pin the operation inputs. The user may change the selection while a
        // long download is running; the installation must still belong to the
        // mod/file that was chosen when the command started.
        var card = SelectedCard;
        var version = SelectedVersion;
        var file = SelectedFile;

        IsDownloading = true;
        DownloadStatus = "正在获取下载链接…";
        DownloadProgress = 0;
        try
        {
            var links = await _nexus.GetDownloadLinksAsync(
                _settings.Current.NexusGameDomain,
                version.GameScopedId);

            var link = links.FirstOrDefault(candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out _));
            if (link is null)
            {
                _shell.SetStatus("Nexus 没有返回可用的下载链接。", StatusKind.Error);
                return;
            }

            DownloadStatus = "正在下载…";
            var extension = GetExtension(link.Url);
            var fileName = $"{PathSafety.GetSafeFileName(card.Summary.Name)}-{PathSafety.GetSafeFileName(version.Version)}{extension}";
            var destination = Path.Combine(AppPaths.DownloadsDirectory, fileName);

            var progress = new Progress<double>(value => DownloadProgress = value);
            var download = await _downloader.DownloadAsync(link.Url, destination, progress);
            if (!download.Success)
            {
                _shell.SetStatus(download.Message, StatusKind.Error);
                return;
            }

            DownloadStatus = "正在安装…";
            var source = PackageSource.Nexus(
                card.Summary.Name,
                _settings.Current.NexusGameDomain,
                card.Summary.GameScopedId,
                card.Summary.Id,
                file?.Id,
                card.Summary.Version);
            var installed = await _operations.InstallArchiveAsync(installation, destination, source, progress);
            if (installed.Success)
            {
                await RecordSourceAsync(
                    installation,
                    installed,
                    card.Summary.Name,
                    _settings.Current.NexusGameDomain,
                    card.Summary.GameScopedId,
                    card.Summary.Id,
                    card.Summary.Version,
                    file?.Id);
                _shell.SetStatus($"「{card.Name}」安装完成。", StatusKind.Success);
                DownloadStatus = "安装完成。";
            }
            else
            {
                _shell.SetStatus(installed.Message, StatusKind.Error);
                DownloadStatus = "安装未完成。";
            }
        }
        catch (NexusApiException exception)
        {
            if (exception.StatusCode == 403)
            {
                _pendingFreeDownload = new PendingFreeDownload(
                    installation,
                    _settings.Current.NexusGameDomain,
                    long.TryParse(card.Summary.GameScopedId, out var mg) ? mg : 0,
                    long.TryParse(file?.Id, out var fi) ? fi : 0,
                    card.Name,
                    card.Summary.Version,
                    card.Summary.Id,
                    file?.Id);
                DownloadStatus = "普通 API Key 无法获取直链。已打开 Nexus 文件页，请完成 Slow download / Mod manager download，MK 会继续。";
                await OpenNexusFilePageAsync();
                _shell.SetStatus(DownloadStatus, StatusKind.Warning);
                return;
            }

            _shell.SetStatus(exception.Message, StatusKind.Error);
            DownloadStatus = "下载链接不可用；请使用“在 Nexus 打开”。";
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"下载或安装失败：{exception.Message}", StatusKind.Error);
            DownloadStatus = "下载或安装失败。";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void RefreshNxmStatus()
    {
        var status = _nxmRegistrar.GetStatus();
        NxmRegistered = status.RegisteredForCurrentExecutable;
        NxmRegistrationHint = !status.Supported
            ? "当前平台不支持 NXM 协议注册。"
            : status.RegisteredForCurrentExecutable
                ? "NXM 已注册，可以打开浏览器文件页并使用 Mod manager download。"
                : string.IsNullOrWhiteSpace(status.CurrentCommand)
                    ? "还未注册 NXM 链接。请到设置 → 浏览器集成（NXM）注册。"
                    : $"nxm:// 当前由其他程序处理：{status.CurrentCommand}";
    }

    [RelayCommand]
    private async Task OpenNexusFilePageAsync()
    {
        if (SelectedCard is null) return;
        if (!NxmRegistered)
        {
            _shell.SetStatus("还未注册 NXM 链接。请到设置 → 浏览器集成（NXM）注册。", StatusKind.Warning);
            return;
        }

        var domain = _settings.Current.NexusGameDomain;
        var url = $"https://www.nexusmods.com/{domain}/mods/{SelectedCard.Summary.GameScopedId}?tab=files";

        // v3 file ids are not the legacy ids the website expects. Resolve the
        // v1 file id by name/version; if that fails, omit the parameter rather
        // than opening an unrelated file.
        if (SelectedVersion is { } version)
        {
            var legacyFileId = await _nexus.GetLegacyFileIdAsync(
                domain,
                SelectedCard.Summary.GameScopedId,
                version.Name,
                version.Version,
                version.UploadedAt);
            if (legacyFileId is not null)
                url += $"&file_id={legacyFileId.Value}";
        }

        url += "&nmm=1";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开浏览器失败：{exception.Message}", StatusKind.Error);
        }
    }

    public async Task HandleNxmLinkAsync(string raw)
    {
        if (IsDownloading) return;

        if (!NxmLinkParser.TryParse(raw, out var nxm, out var parseError) || nxm is null)
        {
            _shell.SetStatus(parseError, StatusKind.Error);
            DownloadStatus = parseError;
            return;
        }

        if (!string.Equals(nxm.GameDomain, _settings.Current.NexusGameDomain, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"NXM 游戏域是「{nxm.GameDomain}」，当前设置为「{_settings.Current.NexusGameDomain}」。请检查设置。";
            _shell.SetStatus(message, StatusKind.Error);
            DownloadStatus = message;
            return;
        }

        if (nxm.IsExpired(DateTimeOffset.UtcNow))
        {
            const string message = "NXM 下载授权已过期。请回到 Nexus 文件页重新点击“Mod manager download”。";
            _shell.SetStatus(message, StatusKind.Error);
            DownloadStatus = message;
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey))
        {
            const string message = "处理 NXM 链接仍需要 Nexus API Key，请先在设置中填写。";
            _shell.SetStatus(message, StatusKind.Warning);
            DownloadStatus = message;
            return;
        }

        if (nxm.UserId is { } nxmUserId)
        {
            try
            {
                var account = await _nexus.GetAccountStatusAsync();
                if (account.UserId is { } accountUserId && accountUserId != nxmUserId)
                {
                    const string message = "这个 NXM 链接属于另一个 Nexus 账号，已拒绝。请用当前账号重新获取链接。";
                    _shell.SetStatus(message, StatusKind.Error);
                    DownloadStatus = message;
                    return;
                }
            }
            catch (NexusApiException exception)
            {
                _shell.SetStatus(exception.Message, StatusKind.Error);
                DownloadStatus = exception.Message;
                return;
            }
        }

        var pending = _pendingFreeDownload;
        var matchesPending = pending is not null
            && pending.GameDomain.Equals(nxm.GameDomain, StringComparison.OrdinalIgnoreCase)
            && pending.ModId == nxm.ModId
            && pending.FileId == nxm.FileId;

        GameInstallation? installation = matchesPending ? pending!.Installation : _shell.Game.Selected;
        if (installation is null)
        {
            _shell.SetStatus("请先选择游戏安装。", StatusKind.Warning);
            return;
        }

        if (!installation.HasBepInEx)
        {
            _shell.SetStatus("当前游戏没有安装 BepInEx，无法安装模组。", StatusKind.Warning);
            return;
        }

        if (matchesPending) _pendingFreeDownload = null;

        IsDownloading = true;
        HasNxmFallback = false;
        _nxmFallbackUrl = null;
        DownloadStatus = "正在通过 NXM 获取下载链接…";
        DownloadProgress = 0;
        try
        {
            var links = await _nexus.GetNxmDownloadLinksAsync(
                nxm.GameDomain,
                nxm.ModId,
                nxm.FileId,
                nxm.Key,
                nxm.Expires);

            var link = links.FirstOrDefault(candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out _));
            if (link is null)
            {
                _shell.SetStatus("Nexus 没有返回可用的 NXM 下载链接。", StatusKind.Error);
                return;
            }

            DownloadStatus = "正在下载 NXM 文件…";
            _nxmFallbackUrl = link.Url;
            HasNxmFallback = true;
            var extension = GetExtension(link.Url);
            var fileName = $"nxm-{nxm.ModId}-{nxm.FileId}{extension}";
            var destination = Path.Combine(AppPaths.DownloadsDirectory, fileName);

            var progress = new Progress<double>(value => DownloadProgress = value);
            var download = await _downloader.DownloadAsync(link.Url, destination, progress);
            if (!download.Success)
            {
                _shell.SetStatus(download.Message, StatusKind.Error);
                DownloadStatus = download.Message + " 可点“浏览器打开直链”重试。";
                return;
            }

            HasNxmFallback = false;
            _nxmFallbackUrl = null;

            DownloadStatus = "正在读取 Nexus 元数据…";
            var nexusName = matchesPending ? pending!.NexusName : $"Nexus 模组 #{nxm.ModId}";
            var nexusVersion = matchesPending ? pending!.Version : null;
            var modUid = matchesPending ? pending!.ModUid : string.Empty;
            if (!matchesPending)
            {
                try
                {
                    var details = await _nexus.GetModAsync(nxm.GameDomain, nxm.ModId.ToString());
                    nexusName = details.Summary.Name;
                    nexusVersion = details.Summary.Version;
                    modUid = details.Summary.Id;
                }
                catch (Exception exception)
                {
                    _shell.SetStatus($"读取 Nexus 名称失败：{exception.Message}", StatusKind.Warning);
                }
            }

            DownloadStatus = "正在安装 NXM 文件…";
            var source = PackageSource.Nexus(
                nexusName,
                nxm.GameDomain,
                nxm.ModId.ToString(),
                modUid,
                nxm.FileId.ToString(),
                nexusVersion);
            var installed = await _operations.InstallArchiveAsync(installation, destination, source, progress);
            if (installed.Success)
            {
                await RecordSourceAsync(
                    installation,
                    installed,
                    nexusName,
                    nxm.GameDomain,
                    nxm.ModId.ToString(),
                    modUid,
                    nexusVersion,
                    nxm.FileId.ToString());
            }

            _shell.SetStatus(installed.Message, installed.Success ? StatusKind.Success : StatusKind.Error);
            DownloadStatus = installed.Success ? "安装完成。" : "安装未完成。";
        }
        catch (NexusApiException exception)
        {
            _shell.SetStatus(exception.Message, StatusKind.Error);
            DownloadStatus = exception.Message;
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"NXM 下载失败：{exception.Message}", StatusKind.Error);
            DownloadStatus = "NXM 下载失败。";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private async Task RecordSourceAsync(
        GameInstallation installation,
        ModInstallResult result,
        string nexusName,
        string gameDomain,
        string gameScopedId,
        string modUid,
        string? version,
        string? fileId)
    {
        var sources = result.Packages
            .Where(package => package.Success)
            .Select(package => new InstalledSource(
                installation.GameDirectory,
                package.PackageName,
                nexusName,
                gameDomain,
                gameScopedId,
                modUid,
                version,
                fileId,
                DateTimeOffset.UtcNow))
            .ToList();

        await _sources.RecordAsync(sources);
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

    partial void OnSelectedFileChanged(NexusModFile? value)
    {
        if (value is null || _suppressFileSelection) return;
        _ = LoadVersionsAsync(value, ++_loadVersion);
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

            Categories.Clear();
            Categories.Add(AllCategories);
            foreach (var category in categories) Categories.Add(category);
            if (!Categories.Contains(SelectedCategory)) SelectedCategory = AllCategories;
            else ApplyCategoryFilter();

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
                string.Equals(card.Summary.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));

        Mods.Clear();
        foreach (var card in filtered) Mods.Add(card);
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
