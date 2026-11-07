using System.Net;
using System.Text;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Infrastructure.Nexus;

namespace ModificationsKnown.Tests;

public sealed class NexusNxmDownloadTests
{
    [Fact]
    public async Task BuildsLegacyDownloadLinkWithEncodedKeyAndAuthHeader()
    {
        Uri? requestedUri = null;
        string? apiKey = null;
        var handler = new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            apiKey = request.Headers.TryGetValues("apikey", out var values) ? values.FirstOrDefault() : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"name\":\"Nexus CDN\",\"short_name\":\"nexus\",\"URI\":\"https://cdn.example.test/file.zip\"}]",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var http = new HttpClient(handler);
        using var client = new NexusModsClient(() => "test-key", null, http);

        var links = await client.GetNxmDownloadLinksAsync(
            "scavprototype",
            67,
            123456,
            "abc+def/ghi",
            1893456000);

        Assert.Single(links);
        Assert.Equal("https://cdn.example.test/file.zip", links[0].Url);
        Assert.Equal("test-key", apiKey);
        Assert.NotNull(requestedUri);
        Assert.StartsWith("https://api.nexusmods.com/v1/games/scavprototype/mods/67/files/123456/download_link.json", requestedUri!.ToString());
        Assert.Contains("key=abc%2Bdef%2Fghi", requestedUri.Query);
        Assert.Contains("expires=1893456000", requestedUri.Query);
    }

    [Fact]
    public async Task MapsForbiddenToNexusApiException()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"detail\":\"nope\"}", Encoding.UTF8, "application/json")
        });

        using var http = new HttpClient(handler);
        using var client = new NexusModsClient(() => "test-key", null, http);

        var exception = await Assert.ThrowsAsync<NexusApiException>(() =>
            client.GetNxmDownloadLinksAsync("scavprototype", 67, 123456, "key", 1893456000));

        Assert.Equal(403, exception.StatusCode);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_respond(request));
        }
    }
}
