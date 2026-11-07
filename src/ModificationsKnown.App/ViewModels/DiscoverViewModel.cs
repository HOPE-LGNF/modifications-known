using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Network;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Storage;

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
    private readonly IModInstaller _installer;
    private readonly ThumbnailService _thumbnails;
    private readonly IFilePickerService _filePicker;

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
    private string _resultSummary = "输入关键词，或直接浏览 Nexus 上的 CU 模组。";

    [ObservableProperty]
    private string _downloadStatus = string.Empty;

    [ObservableProperty]
    private string _nxmLinkText = string.Empty;

    [ObservableProperty]
    private bool _hasNxmFallback;

    private string? _nxmFallbackUrl;

    public DiscoverViewModel(
        IAppShell shell,
        INexusModsClient nexus,
        IAppSettingsService settings,
        IFileDownloader downloader,
        IModInstaller installer,
        ThumbnailService thumbnails,
        IFilePickerService filePicker)
    {
        _shell = shell;
        _nexus = nexus;
        _settings = settings;
        _downloader = downloader;
        _installer = installer;
        _thumbnails = thumbnails;
        _filePicker = filePicker;
        ApiKeyMissing = string.IsNullOrWhiteSpace(settings.Current.NexusApiKey);
        _shell.Game.Changed += (_, _) => OnPropertyChanged(nameof(CanInstall));
    }

    public ObservableCollection<ModCardViewModel> Mods { get; } = [];

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
        return Mods.Count > 0 ? Task.CompletedTask : LoadPageAsync();
    }

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

        IsDownloading = true;
        DownloadStatus = "正在获取下载链接…";
        DownloadProgress = 0;
        try
        {
            var links = await _nexus.GetDownloadLinksAsync(
                _settings.Current.NexusGameDomain,
                SelectedVersion.GameScopedId);

            var link = links.FirstOrDefault(candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out _));
            if (link is null)
            {
                _shell.SetStatus("Nexus 没有返回可用的下载链接。", StatusKind.Error);
                return;
            }

            DownloadStatus = "正在下载…";
            var extension = GetExtension(link.Url);
            var fileName = $"{PathSafety.GetSafeFileName(SelectedCard.Summary.Name)}-{PathSafety.GetSafeFileName(SelectedVersion.Version)}{extension}";
            var destination = Path.Combine(AppPaths.DownloadsDirectory, fileName);

            var progress = new Progress<double>(value => DownloadProgress = value);
            var download = await _downloader.DownloadAsync(link.Url, destination, progress);
            if (!download.Success)
            {
                _shell.SetStatus(download.Message, StatusKind.Error);
                return;
            }

            DownloadStatus = "正在安装…";
            var installed = await _installer.InstallArchiveAsync(installation, destination);
            if (installed.Success)
            {
                _shell.SetStatus($"「{SelectedCard.Name}」安装完成。", StatusKind.Success);
                DownloadStatus = "安装完成。";
            }
            else
            {
                _shell.SetStatus(installed.Message, StatusKind.Error);
                DownloadStatus = installed.Message;
            }
        }
        catch (NexusApiException exception)
        {
            _shell.SetStatus(exception.Message, StatusKind.Error);
            DownloadStatus = exception.StatusCode == 403
                ? "普通 API Key 无法获取直链：请点“浏览器下载”，下载完成后点“安装本地文件”。"
                : "下载链接不可用；请使用“在 Nexus 打开”。";
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

    [RelayCommand]
    private async Task InstallLocalArchiveAsync()
    {
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

        var path = await _filePicker.PickArchiveAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        IsDownloading = true;
        DownloadStatus = "正在安装本地压缩包…";
        DownloadProgress = 0;
        try
        {
            var result = await _installer.InstallArchiveAsync(installation, path);
            _shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
            DownloadStatus = result.Message;
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"安装失败：{exception.Message}", StatusKind.Error);
            DownloadStatus = "安装失败。";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void OpenNexusFilePage()
    {
        if (SelectedCard is null) return;

        var domain = _settings.Current.NexusGameDomain;
        var url = $"https://www.nexusmods.com/{domain}/mods/{SelectedCard.Summary.GameScopedId}?tab=files";
        if (SelectedFile is not null)
            url += $"&file_id={SelectedFile.Id}";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开浏览器失败：{exception.Message}", StatusKind.Error);
        }
    }

    [RelayCommand]
    private async Task ProcessNxmAsync()
    {
        await HandleNxmLinkAsync(NxmLinkText);
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

            DownloadStatus = "正在安装 NXM 文件…";
            var installed = await _installer.InstallArchiveAsync(installation, destination);
            _shell.SetStatus(installed.Message, installed.Success ? StatusKind.Success : StatusKind.Error);
            DownloadStatus = installed.Message;
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
            NxmLinkText = string.Empty;
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
            Mods.Clear();
            foreach (var summary in result.Items)
            {
                var card = new ModCardViewModel(summary);
                Mods.Add(card);
                _ = LoadCardThumbnailAsync(card);
            }

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

    private static string GetExtension(string url)
    {
        var extension = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        return extension is ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2"
            ? extension
            : ".zip";
    }
}
