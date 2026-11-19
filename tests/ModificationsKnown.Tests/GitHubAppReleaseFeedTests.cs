using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ModificationsKnown.Core.Updates;
using ModificationsKnown.Infrastructure.Updates;

namespace ModificationsKnown.Tests;

public sealed class GitHubAppReleaseFeedTests
{
    private const string LatestPath = "/repos/HOPE-LGNF/modifications-known/releases/latest";

    private static string ReleaseObjectJson(
        long id,
        string tag,
        bool prerelease,
        bool draft = false,
        string assetName = "ModificationsKnown-{0}-win-x64.exe") =>
        $$"""
        {
          "id": {{id}},
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/HOPE-LGNF/modifications-known/releases/tag/{{tag}}",
          "assets": [{
            "name": "{{string.Format(assetName, tag)}}",
            "digest": "sha256:{{new string('a', 64)}}",
            "browser_download_url": "https://github.com/HOPE-LGNF/modifications-known/releases/download/{{tag}}/{{string.Format(assetName, tag)}}"
          }]
        }
        """;

    private static string ReleaseJson(
        long id,
        string tag,
        bool prerelease,
        bool draft = false,
        string assetName = "ModificationsKnown-{0}-win-x64.exe") =>
        "[" + ReleaseObjectJson(id, tag, prerelease, draft, assetName) + "]";

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return response;
    }

    [Fact]
    public async Task StableRequestsTheLatestEndpointWithGitHubHeaders()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHandler(request =>
        {
            requests.Add(request);
            return Json(ReleaseObjectJson(11, "v0.7.0", prerelease: false));
        });

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Stable);

        Assert.True(result.IsComplete);
        Assert.Single(requests);
        Assert.Equal(LatestPath, requests[0].RequestUri!.AbsolutePath);
        Assert.Contains(requests[0].Headers.UserAgent,
            value => value.Product?.Name?.Contains("ModificationsKnown") == true);
        Assert.Contains(requests[0].Headers.Accept,
            value => string.Equals(value.MediaType, "application/vnd.github+json", StringComparison.OrdinalIgnoreCase));

        var release = Assert.Single(result.Releases);
        Assert.Equal("v0.7.0", release.Tag);
        Assert.Equal(11, release.Id);
        Assert.False(release.Prerelease);
    }

    [Fact]
    public async Task StableNotFoundMeansNoStableReleaseNotAFailure()
    {
        var handler = new StubHandler(_ => Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Stable);

        Assert.True(result.IsComplete);
        Assert.Empty(result.Releases);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task PreviewFollowsNextLinkAcrossPages()
    {
        var pages = new List<string>();
        var handler = new StubHandler(request =>
        {
            var query = request.RequestUri!.Query;
            pages.Add(request.RequestUri.AbsolutePath + query);

            if (!query.Contains("page=2", StringComparison.OrdinalIgnoreCase))
            {
                var first = Json(ReleaseJson(1, "v0.7.0", prerelease: false));
                first.Headers.TryAddWithoutValidation(
                    "Link",
                    "<https://api.github.com/repos/HOPE-LGNF/modifications-known/releases?per_page=100&page=2>; rel=\"next\"");
                return first;
            }

            return Json(ReleaseJson(2, "v0.8.0-beta.1", prerelease: true));
        });

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Preview);

        Assert.True(result.IsComplete);
        Assert.Equal(2, pages.Count);
        Assert.Contains(result.Releases, release => release.Tag == "v0.7.0");
        Assert.Contains(result.Releases, release => release.Tag == "v0.8.0-beta.1");
    }

    [Fact]
    public async Task PreviewRejectsANextLinkOutsideTheRepository()
    {
        var handler = new StubHandler(_ =>
        {
            var response = Json(ReleaseJson(1, "v0.7.0", prerelease: false));
            response.Headers.TryAddWithoutValidation(
                "Link",
                "<https://evil.example/repos/HOPE-LGNF/modifications-known/releases?page=2>; rel=\"next\"");
            return response;
        });

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Preview);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task PreviewStopsAtThePageLimitAndReportsIncomplete()
    {
        var requests = 0;
        var handler = new StubHandler(request =>
        {
            requests++;
            var page = requests + 1; // the feed asks for page 1 first, then follows next
            var response = Json(ReleaseJson(1, $"v0.{requests}.0", prerelease: false));
            response.Headers.TryAddWithoutValidation(
                "Link",
                $"<https://api.github.com/repos/HOPE-LGNF/modifications-known/releases?per_page=100&page={page}>; rel=\"next\"");
            return response;
        });

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Preview);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.Error);
        Assert.InRange(requests, 2, 11);
    }

    [Fact]
    public async Task RateLimitIsAFailureNotAnEmptyFeed()
    {
        var handler = new StubHandler(_ => Json("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden));

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Stable);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.Error);
        Assert.Contains("限流", result.Error);
    }

    [Fact]
    public async Task ServerErrorIsAFailure()
    {
        var handler = new StubHandler(_ => Json("""{"message":"boom"}""", HttpStatusCode.InternalServerError));

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Stable);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task MalformedJsonIsAFailure()
    {
        var handler = new StubHandler(_ => Json("not json at all"));

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Preview);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task PreviewFailsWhenALaterPageFails()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Query.Contains("page=2", StringComparison.OrdinalIgnoreCase))
                return Json("""{"message":"boom"}""", HttpStatusCode.InternalServerError);

            var first = Json(ReleaseJson(1, "v0.7.0", prerelease: false));
            first.Headers.TryAddWithoutValidation(
                "Link",
                "<https://api.github.com/repos/HOPE-LGNF/modifications-known/releases?per_page=100&page=2>; rel=\"next\"");
            return first;
        });

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Preview);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Releases);
    }

    [Fact]
    public async Task PreviewRejectsALoopingPagingLink()
    {
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            var response = Json(ReleaseJson(1, "v0.7.0", prerelease: false));
            response.Headers.TryAddWithoutValidation(
                "Link",
                "<https://api.github.com/repos/HOPE-LGNF/modifications-known/releases?per_page=100>; rel=\"next\"");
            return response;
        });

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Preview);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.Error);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task OversizedResponseIsAFailure()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
            response.Content.Headers.ContentLength = 8 * 1024 * 1024;
            return response;
        });

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        var result = await feed.GetAsync(AppUpdateChannel.Preview);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task CancellationIsReportedAsFailure()
    {
        var handler = new StubHandler(_ => Json(ReleaseJson(1, "v0.7.0", prerelease: false)));

        using var http = new HttpClient(handler);
        var feed = new GitHubAppReleaseFeed(http);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await feed.GetAsync(AppUpdateChannel.Preview, cts.Token);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.Error);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }
    }
}
