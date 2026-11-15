using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using ModificationsKnown.Core.Common;

namespace ModificationsKnown.App.Services;

public sealed record AppUpdateCheck(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string? AssetUrl,
    string Message);

/// <summary>Checks the public GitHub release feed. Network failures and rate
/// limits are reported as a message, never as an update.</summary>
public sealed class AppUpdateService
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/HOPE-LGNF/modifications-known/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public string CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null
                ? "0.0.0"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public async Task<AppUpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("ModificationsKnown/" + CurrentVersion);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AppUpdateCheck(
                    false, CurrentVersion, CurrentVersion, string.Empty, null,
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? "GitHub API 限流，暂时无法检查更新；请稍后再试。"
                        : $"GitHub 返回 {(int)response.StatusCode}，暂时无法检查更新。");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;
            var latest = VersionText.Label(tag).TrimStart('v', 'V');
            var releaseUrl = root.TryGetProperty("html_url", out var html)
                ? html.GetString() ?? string.Empty
                : string.Empty;

            string? assetUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var candidates = assets.EnumerateArray()
                    .Select(asset => new
                    {
                        Url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null,
                        Name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty
                    })
                    .Where(asset => !string.IsNullOrWhiteSpace(asset.Url)
                                    && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                assetUrl = candidates
                    .FirstOrDefault(asset => asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                    ?.Url
                    ?? candidates.FirstOrDefault()?.Url;
            }

            if (!VersionText.IsNewer(latest, CurrentVersion))
            {
                return new AppUpdateCheck(
                    false, CurrentVersion, CurrentVersion, releaseUrl, null,
                    $"已是最新版本（v{CurrentVersion}）。");
            }

            return new AppUpdateCheck(
                true, CurrentVersion, latest, releaseUrl, assetUrl,
                $"发现新版本 v{latest}（当前 v{CurrentVersion}）。" +
                (assetUrl is null ? " 这个 Release 没有可自动安装的 exe，请打开 Release 页。" : string.Empty));
        }
        catch (Exception exception)
        {
            return new AppUpdateCheck(
                false, CurrentVersion, CurrentVersion, string.Empty, null,
                $"检查更新失败：{exception.Message}");
        }
    }

    public async Task<bool> DownloadAsync(
        string url,
        string destination,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;

            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(destination);
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                if (total is > 0) progress?.Report(copied / (double)total.Value);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
