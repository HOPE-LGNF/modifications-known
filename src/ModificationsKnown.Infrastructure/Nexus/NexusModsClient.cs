using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModificationsKnown.Core.Nexus;

namespace ModificationsKnown.Infrastructure.Nexus;

/// <summary>
/// Nexus Mods API v3 client. The v3 API is currently marked experimental by
/// Nexus; all endpoint shapes are isolated in this class so they can be updated
/// without touching the UI or domain model.
/// </summary>
public sealed class NexusModsClient : INexusModsClient, IDisposable
{
    private const string BaseAddress = "https://api.nexusmods.com/v3/";
    private const string GraphQlAddress = "https://api.nexusmods.com/v2/graphql";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;

    public NexusModsClient(Func<string?> apiKeyProvider, HttpClient? httpClient = null)
    {
        _apiKeyProvider = apiKeyProvider;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri(BaseAddress);
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ModificationsKnown/0.1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<NexusSearchResult> SearchModsAsync(
        string gameDomain,
        NexusSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        query = query.Normalize();

        try
        {
            return await SearchModsGraphQlAsync(gameDomain, query, cancellationToken);
        }
        catch (NexusApiException)
        {
            // v3 is the documented API; keep it as a fallback if the GraphQL
            // search shape changes. Text filtering is then page-local.
            return await SearchModsV3Async(gameDomain, query, cancellationToken);
        }
    }

    private async Task<NexusSearchResult> SearchModsGraphQlAsync(
        string gameDomain,
        NexusSearchQuery query,
        CancellationToken cancellationToken)
    {
        var filter = new Dictionary<string, object?>
        {
            ["gameDomainName"] = new[] { new { value = gameDomain, op = "EQUALS" } }
        };

        if (!string.IsNullOrWhiteSpace(query.Text))
            filter["name"] = new[] { new { value = query.Text, op = "WILDCARD" } };

        if (!query.IncludeAdult)
            filter["adultContent"] = new[] { new { value = false, op = "EQUALS" } };

        var sortField = query.Sort switch
        {
            NexusModSort.Downloads => "downloads",
            NexusModSort.New => "createdAt",
            NexusModSort.Updated => "updatedAt",
            _ => "endorsements"
        };

        var payload = new
        {
            query = """
                query SearchMods($filter: ModsFilter, $sort: [ModsSort!], $count: Int, $offset: Int) {
                  mods(filter: $filter, sort: $sort, count: $count, offset: $offset) {
                    totalCount
                    nodes {
                      uid
                      modId
                      name
                      summary
                      version
                      author
                      thumbnailUrl
                      downloads
                      endorsements
                      fileSize
                      adultContent
                      supportsVortex
                      updatedAt
                      modCategory { name }
                    }
                  }
                }
                """,
            variables = new
            {
                filter,
                sort = new[] { new Dictionary<string, object> { [sortField] = new { direction = "DESC" } } },
                count = query.PageSize,
                offset = (query.Page - 1) * query.PageSize
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlAddress)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        var apiKey = _apiKeyProvider();
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("apikey", apiKey.Trim());

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateException(response.StatusCode, body);

        var envelope = JsonSerializer.Deserialize<GraphQlEnvelope<GraphQlModsData>>(body, JsonOptions);
        if (envelope?.Errors is { Count: > 0 })
            throw new NexusApiException("Nexus 搜索暂时不可用：" + envelope.Errors[0].Message, (int)response.StatusCode, body);
        if (envelope?.Data?.Mods is null)
            throw new NexusApiException("Nexus 搜索返回了空结果。", (int)response.StatusCode, body);

        var nodes = envelope.Data.Mods.Nodes;
        var mods = nodes.Select(node => new NexusModSummary(
                node.Uid,
                node.ModId.ToString(),
                node.Name,
                node.Summary,
                node.Version,
                node.Author,
                node.ModCategory?.Name,
                node.ThumbnailUrl,
                node.Endorsements,
                node.Downloads,
                node.FileSize,
                node.UpdatedAt,
                node.AdultContent,
                node.SupportsVortex))
            .ToList();

        return new NexusSearchResult(mods, query.Page, query.PageSize, envelope.Data.Mods.TotalCount);
    }

    private async Task<NexusSearchResult> SearchModsV3Async(
        string gameDomain,
        NexusSearchQuery query,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            filters = new
            {
                games = new[] { gameDomain },
                adult = query.IncludeAdult ? "include" : "exclude"
            },
            sort = new
            {
                field = query.Sort switch
                {
                    NexusModSort.Downloads => "downloads",
                    NexusModSort.New => "new",
                    NexusModSort.Updated => "updated",
                    _ => "endorsements"
                },
                direction = "desc"
            },
            pagination = new
            {
                type = "page",
                page = query.Page,
                page_size = query.PageSize
            }
        };

        var envelope = await SendAsync<SearchData>(
            HttpMethod.Post,
            "mods/search",
            payload,
            cancellationToken);

        var mods = envelope.Data?.Mods.Select(MapSummary).ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            mods = mods
                .Where(mod =>
                    mod.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
                    || (mod.Summary?.Contains(query.Text, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (mod.Author?.Contains(query.Text, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        var meta = envelope.Meta;
        return new NexusSearchResult(
            mods,
            meta?.Page ?? query.Page,
            meta?.PageSize ?? query.PageSize,
            string.IsNullOrWhiteSpace(query.Text) ? meta?.TotalCount ?? mods.Count : mods.Count);
    }

    public async Task<NexusModDetails> GetModAsync(
        string gameDomain,
        string gameScopedId,
        CancellationToken cancellationToken = default)
    {
        var envelope = await SendAsync<ModDetailsDto>(
            HttpMethod.Get,
            $"games/{Uri.EscapeDataString(gameDomain)}/mods/{Uri.EscapeDataString(gameScopedId)}",
            null,
            cancellationToken);

        var mod = envelope.Data ?? throw new NexusApiException("Nexus 返回了空的模组详情。");
        var stats = mod.Stats ?? new StatsDto();
        var summary = new NexusModSummary(
            mod.Id,
            mod.GameScopedId,
            mod.Name,
            null,
            mod.Version,
            mod.Author,
            mod.Category?.Name,
            mod.PrimaryImage?.ThumbnailUrl ?? mod.PrimaryImage?.Url,
            stats.Endorsements,
            stats.Downloads,
            0,
            null,
            mod.AdultContent,
            false);
        var gallery = mod.GalleryImages?
            .Select(image => image.Url)
            .OfType<string>()
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToList() ?? [];

        return new NexusModDetails(summary, mod.Description, gallery, mod.AdultContent);
    }

    public async Task<IReadOnlyList<NexusModFile>> GetFilesAsync(
        string modId,
        CancellationToken cancellationToken = default)
    {
        var envelope = await SendAsync<ModFilesData>(
            HttpMethod.Get,
            $"mods/{Uri.EscapeDataString(modId)}/files",
            null,
            cancellationToken);

        return envelope.Data?.ModFiles?
            .Select(file => new NexusModFile(
                file.Id,
                file.Name,
                file.IsActive,
                file.LastFileUploadedAt,
                file.VersionsCount))
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<NexusFileVersion>> GetFileVersionsAsync(
        string modFileId,
        CancellationToken cancellationToken = default)
    {
        var envelope = await SendAsync<FileVersionsData>(
            HttpMethod.Get,
            $"mod-files/{Uri.EscapeDataString(modFileId)}/versions",
            null,
            cancellationToken);

        return envelope.Data?.Versions?
            .Select(version => new NexusFileVersion(
                version.Id,
                version.GameScopedId,
                version.Name,
                version.Version,
                version.Category,
                version.UploadedAt,
                version.IsPrimary))
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(
        string gameDomain,
        string fileVersionGameScopedId,
        CancellationToken cancellationToken = default)
    {
        var envelope = await SendAsync<DownloadLinksData>(
            HttpMethod.Get,
            $"games/{Uri.EscapeDataString(gameDomain)}/mod-file-versions/{Uri.EscapeDataString(fileVersionGameScopedId)}/download-link",
            null,
            cancellationToken);

        return envelope.Data?.Links?
            .Select(link => new NexusDownloadLink(link.Name, link.Url))
            .ToList() ?? [];
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<ApiEnvelope<T>> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativePath);

        var apiKey = _apiKeyProvider();
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("apikey", apiKey.Trim());

        if (payload is not null)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw CreateException(response.StatusCode, body);

        try
        {
            var envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(body, JsonOptions);
            return envelope ?? throw new NexusApiException("Nexus 返回了无法解析的响应。");
        }
        catch (JsonException exception)
        {
            throw new NexusApiException("Nexus 返回的 JSON 结构已变化，暂时无法解析。", (int)response.StatusCode, body, exception);
        }
    }

    private static NexusApiException CreateException(HttpStatusCode status, string body)
    {
        var message = status switch
        {
            HttpStatusCode.Unauthorized => "Nexus API Key 无效或已过期。请在设置中重新填写。",
            HttpStatusCode.Forbidden => "Nexus 拒绝了该请求。免费账号通常无法直接生成下载链接；请使用“在 Nexus 打开”，或配置 Premium API Key。",
            HttpStatusCode.NotFound => "Nexus 上找不到该模组或文件。",
            HttpStatusCode.TooManyRequests => "Nexus API 请求过于频繁，请稍后再试。",
            _ => $"Nexus 请求失败（HTTP {(int)status}）。"
        };

        return new NexusApiException(message, (int)status, body);
    }

    private static NexusModSummary MapSummary(ModSummaryDto mod) => new(
        mod.Id,
        mod.GameScopedId,
        mod.Name,
        mod.Summary,
        mod.Version,
        mod.Author?.Name,
        mod.Category?.Name,
        mod.ThumbnailUrl,
        mod.Endorsements,
        mod.Downloads,
        mod.FileSizeKilobytes,
        mod.UpdatedAt,
        mod.Adult,
        mod.SupportsVortex);

    private sealed class ApiEnvelope<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; init; }

        [JsonPropertyName("meta")]
        public MetaDto? Meta { get; init; }
    }

    private sealed class MetaDto
    {
        [JsonPropertyName("page")]
        public int Page { get; init; }

        [JsonPropertyName("page_size")]
        public int PageSize { get; init; }

        [JsonPropertyName("total_count")]
        public int TotalCount { get; init; }
    }

    private sealed class SearchData
    {
        [JsonPropertyName("mods")]
        public List<ModSummaryDto> Mods { get; init; } = [];
    }

    private sealed class ModFilesData
    {
        [JsonPropertyName("mod_files")]
        public List<ModFileDto> ModFiles { get; init; } = [];
    }

    private sealed class FileVersionsData
    {
        [JsonPropertyName("versions")]
        public List<FileVersionDto> Versions { get; init; } = [];
    }

    private sealed class DownloadLinksData
    {
        [JsonPropertyName("links")]
        public List<DownloadLinkDto> Links { get; init; } = [];
    }

    private class ModSummaryDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("game_scoped_id")]
        public string GameScopedId { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("author")]
        public AuthorDto? Author { get; init; }

        [JsonPropertyName("category")]
        public CategoryDto? Category { get; init; }

        [JsonPropertyName("thumbnail_url")]
        public string? ThumbnailUrl { get; init; }

        [JsonPropertyName("endorsements")]
        public long Endorsements { get; init; }

        [JsonPropertyName("downloads")]
        public long Downloads { get; init; }

        [JsonPropertyName("file_size_kilobytes")]
        public long FileSizeKilobytes { get; init; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; init; }

        [JsonPropertyName("adult")]
        public bool Adult { get; init; }

        [JsonPropertyName("supports_vortex")]
        public bool SupportsVortex { get; init; }
    }

    private sealed class ModDetailsDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("game_scoped_id")]
        public string GameScopedId { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("author")]
        public string? Author { get; init; }

        [JsonPropertyName("category")]
        public CategoryDto? Category { get; init; }

        [JsonPropertyName("adult_content")]
        public bool AdultContent { get; init; }

        [JsonPropertyName("stats")]
        public StatsDto? Stats { get; init; }

        [JsonPropertyName("primary_image")]
        public ImageDto? PrimaryImage { get; init; }

        [JsonPropertyName("gallery_images")]
        public List<ImageDto>? GalleryImages { get; init; }
    }

    private sealed class StatsDto
    {
        [JsonPropertyName("downloads")]
        public long Downloads { get; init; }

        [JsonPropertyName("endorsements")]
        public long Endorsements { get; init; }
    }

    private sealed class ImageDto
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("thumbnail_url")]
        public string? ThumbnailUrl { get; init; }
    }

    private sealed class ModFileDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("is_active")]
        public bool IsActive { get; init; }

        [JsonPropertyName("last_file_uploaded_at")]
        public DateTimeOffset? LastFileUploadedAt { get; init; }

        [JsonPropertyName("versions_count")]
        public int VersionsCount { get; init; }
    }

    private sealed class FileVersionDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("game_scoped_id")]
        public string GameScopedId { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; init; } = string.Empty;

        [JsonPropertyName("uploaded_at")]
        public DateTimeOffset? UploadedAt { get; init; }

        [JsonPropertyName("is_primary")]
        public bool IsPrimary { get; init; }
    }

    private sealed class DownloadLinkDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;
    }

    private sealed class GraphQlEnvelope<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; init; }

        [JsonPropertyName("errors")]
        public List<GraphQlError>? Errors { get; init; }
    }

    private sealed class GraphQlError
    {
        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;
    }

    private sealed class GraphQlModsData
    {
        [JsonPropertyName("mods")]
        public GraphQlModPage? Mods { get; init; }
    }

    private sealed class GraphQlModPage
    {
        [JsonPropertyName("totalCount")]
        public int TotalCount { get; init; }

        [JsonPropertyName("nodes")]
        public List<GraphQlModNode> Nodes { get; init; } = [];
    }

    private sealed class GraphQlModNode
    {
        [JsonPropertyName("uid")]
        public string Uid { get; init; } = string.Empty;

        [JsonPropertyName("modId")]
        public long ModId { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("author")]
        public string? Author { get; init; }

        [JsonPropertyName("thumbnailUrl")]
        public string? ThumbnailUrl { get; init; }

        [JsonPropertyName("downloads")]
        public long Downloads { get; init; }

        [JsonPropertyName("endorsements")]
        public long Endorsements { get; init; }

        [JsonPropertyName("fileSize")]
        public long FileSize { get; init; }

        [JsonPropertyName("adultContent")]
        public bool AdultContent { get; init; }

        [JsonPropertyName("supportsVortex")]
        public bool SupportsVortex { get; init; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; init; }

        [JsonPropertyName("modCategory")]
        public CategoryDto? ModCategory { get; init; }
    }

    private sealed class AuthorDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class CategoryDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}
