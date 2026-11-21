using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ModificationsKnown.Core.Updates;

namespace ModificationsKnown.Infrastructure.Updates;

/// <summary>
/// GitHub REST adapter for the launcher's own release feed. The repository is
/// fixed on purpose; callers cannot point it elsewhere, and a paging link that
/// leaves that repository is treated as a failed check rather than followed.
/// </summary>
public sealed class GitHubAppReleaseFeed : IAppReleaseFeed
{
    private const string RepositorySlug = ReleaseAssetPolicy.RepositorySlug;
    private const string ApiRoot = "https://api.github.com/repos/" + RepositorySlug;
    private const string ReleasesPath = "/repos/" + RepositorySlug + "/releases";
    private const int MaxPages = 10;
    private const int MaxEntries = 600;
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;

    public GitHubAppReleaseFeed(HttpClient httpClient) =>
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<ReleaseFeedResult> GetAsync(
        AppUpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();

        try
        {
            return channel == AppUpdateChannel.Stable
                ? await GetLatestAsync(diagnostics, cancellationToken)
                : await GetAllAsync(diagnostics, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ReleaseFeedResult.Failed("检查已取消。", diagnostics);
        }
        catch (HttpRequestException exception)
        {
            return ReleaseFeedResult.Failed($"无法连接 GitHub：{exception.Message}", diagnostics);
        }
        catch (Exception exception)
        {
            return ReleaseFeedResult.Failed($"检查更新失败：{exception.Message}", diagnostics);
        }
    }

    private async Task<ReleaseFeedResult> GetLatestAsync(
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(ApiRoot + "/releases/latest", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            diagnostics.Add("stable: GitHub reports no published stable release");
            return ReleaseFeedResult.Complete(Array.Empty<ReleaseInfo>(), diagnostics);
        }

        var failure = DescribeFailure(response, diagnostics);
        if (failure is not null) return ReleaseFeedResult.Failed(failure, diagnostics);

        var body = await ReadBodyAsync(response, cancellationToken);
        if (body is null)
        {
            return ReleaseFeedResult.Failed("GitHub 返回的 Release 数据过大，已停止检查。", diagnostics);
        }

        if (!TryParseRelease(body, out var release))
        {
            return ReleaseFeedResult.Failed("无法解析 GitHub 返回的 Release 数据。", diagnostics);
        }

        return ReleaseFeedResult.Complete([release], diagnostics);
    }

    private async Task<ReleaseFeedResult> GetAllAsync(
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var releases = new List<ReleaseInfo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var url = ApiRoot + "/releases?per_page=100";
        var stopwatch = Stopwatch.StartNew();

        for (var page = 1; page <= MaxPages; page++)
        {
            if (stopwatch.Elapsed > TotalBudget)
            {
                return ReleaseFeedResult.Failed("检查超时，未能读取全部 Release。", diagnostics);
            }

            if (!visited.Add(url))
            {
                return ReleaseFeedResult.Failed("GitHub 分页链接重复，已停止检查。", diagnostics);
            }

            using var response = await SendAsync(url, cancellationToken);

            var failure = DescribeFailure(response, diagnostics);
            if (failure is not null) return ReleaseFeedResult.Failed(failure, diagnostics);

            var body = await ReadBodyAsync(response, cancellationToken);
            if (body is null)
            {
                return ReleaseFeedResult.Failed("GitHub 返回的 Release 数据过大，已停止检查。", diagnostics);
            }

            if (!TryParseReleaseArray(body, out var pageReleases))
            {
                return ReleaseFeedResult.Failed("无法解析 GitHub 返回的 Release 数据。", diagnostics);
            }

            releases.AddRange(pageReleases);
            if (releases.Count > MaxEntries)
            {
                return ReleaseFeedResult.Failed(
                    $"Release 数量超过 {MaxEntries} 条上限，已停止检查。", diagnostics);
            }

            var next = ReadNextLink(response);
            if (next is null)
            {
                return ReleaseFeedResult.Complete(releases, diagnostics);
            }

            if (!IsAllowedNextLink(next))
            {
                diagnostics.Add($"rejected paging link: {next}");
                return ReleaseFeedResult.Failed("GitHub 分页链接不在本仓库范围内，已停止检查。", diagnostics);
            }

            url = next;
        }

        return ReleaseFeedResult.Failed(
            $"分页超过 {MaxPages} 页上限，未能读取全部 Release。", diagnostics);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ModificationsKnown");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
    }

    private static string? DescribeFailure(HttpResponseMessage response, List<string> diagnostics)
    {
        if (response.IsSuccessStatusCode) return null;

        var code = (int)response.StatusCode;
        diagnostics.Add($"HTTP {code}");

        if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
        {
            return "GitHub API 限流或拒绝访问，暂时无法检查更新；请稍后再试。";
        }

        return $"GitHub 返回 {code}，暂时无法检查更新。";
    }

    private static async Task<string?> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxBodyBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;

            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? ReadNextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var headers)) return null;

        foreach (var header in headers)
        {
            foreach (var part in header.Split(','))
            {
                var segments = part.Split(';');
                if (segments.Length < 2) continue;

                var target = segments[0].Trim();
                if (target.Length < 2 || target[0] != '<' || target[^1] != '>') continue;

                foreach (var parameter in segments.Skip(1))
                {
                    var trimmed = parameter.Trim();
                    if (!trimmed.StartsWith("rel=", StringComparison.OrdinalIgnoreCase)) continue;

                    var relation = trimmed[4..].Trim().Trim('"');
                    if (relation.Equals("next", StringComparison.OrdinalIgnoreCase))
                    {
                        return target[1..^1];
                    }
                }
            }
        }

        return null;
    }

    private static bool IsAllowedNextLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(uri.AbsolutePath, ReleasesPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseReleaseArray(string json, out List<ReleaseInfo> releases)
    {
        releases = [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (TryParseRelease(element, out var release)) releases.Add(release);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseRelease(string json, out ReleaseInfo release)
    {
        release = default!;

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryParseRelease(document.RootElement, out release);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseRelease(JsonElement element, out ReleaseInfo release)
    {
        release = default!;
        if (element.ValueKind != JsonValueKind.Object) return false;

        if (!element.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
            return false;

        var tagName = tag.GetString();
        if (string.IsNullOrWhiteSpace(tagName)) return false;

        var draft = element.TryGetProperty("draft", out var draftElement)
                    && draftElement.ValueKind == JsonValueKind.True;
        var prerelease = element.TryGetProperty("prerelease", out var preElement)
                         && preElement.ValueKind == JsonValueKind.True;
        var htmlUrl = element.TryGetProperty("html_url", out var htmlElement)
                      && htmlElement.ValueKind == JsonValueKind.String
            ? htmlElement.GetString() ?? string.Empty
            : string.Empty;
        var id = element.TryGetProperty("id", out var idElement)
                 && idElement.TryGetInt64(out var parsedId)
            ? parsedId
            : 0;

        var assets = new List<ReleaseAsset>();
        if (element.TryGetProperty("assets", out var assetsElement)
            && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;

                var name = asset.TryGetProperty("name", out var nameElement)
                           && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                var digest = asset.TryGetProperty("digest", out var digestElement)
                             && digestElement.ValueKind == JsonValueKind.String
                    ? digestElement.GetString()
                    : null;
                var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                                  && urlElement.ValueKind == JsonValueKind.String
                    ? urlElement.GetString()
                    : null;

                assets.Add(new ReleaseAsset(name ?? string.Empty, digest, downloadUrl));
            }
        }

        release = new ReleaseInfo(tagName, draft, prerelease, htmlUrl, assets, id);
        return true;
    }
}
