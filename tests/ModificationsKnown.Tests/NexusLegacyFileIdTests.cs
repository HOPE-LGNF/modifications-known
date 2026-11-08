using System.Net;
using System.Text;
using ModificationsKnown.Infrastructure.Nexus;

namespace ModificationsKnown.Tests;

public sealed class NexusLegacyFileIdTests
{
    [Fact]
    public async Task ResolvesWebsiteFileIdFromV1Files()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"files\":[{\"file_id\":905,\"name\":\"Multiplayer Sprite Replacer\",\"version\":\"1.0\",\"mod_version\":\"1.0\",\"uploaded_timestamp\":1700000000,\"is_primary\":true}]}",
                Encoding.UTF8,
                "application/json")
        });

        using var http = new HttpClient(handler);
        using var client = new NexusModsClient(() => "test-key", null, http);

        var id = await client.GetLegacyFileIdAsync(
            "scavprototype",
            "74",
            "Multiplayer Sprite Replacer",
            "1.0",
            DateTimeOffset.FromUnixTimeSeconds(1700000000));

        Assert.Equal(905, id);
    }

    [Fact]
    public async Task ResolvesVersionWithVPrefixAndDifferentFileDisplayName()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"files\":[{\"file_id\":284,\"name\":\"Multiplayer Sprite Replacer\",\"version\":\"1.1.2\",\"uploaded_timestamp\":1700000000,\"is_primary\":false},{\"file_id\":905,\"name\":\"Multiplayer Sprite Replacer 1.2.0\",\"version\":\"1.2.0\",\"uploaded_timestamp\":1800000000,\"is_primary\":false}]}",
                Encoding.UTF8,
                "application/json")
        });

        using var http = new HttpClient(handler);
        using var client = new NexusModsClient(() => "test-key", null, http);

        var id = await client.GetLegacyFileIdAsync(
            "scavprototype",
            "74",
            "Multiplayer Sprite Replacer",
            "v1.2.0",
            null);

        Assert.Equal(905, id);
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
            CancellationToken cancellationToken) => Task.FromResult(_respond(request));
    }
}
