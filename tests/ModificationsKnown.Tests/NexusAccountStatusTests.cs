using System.Net;
using System.Text;
using ModificationsKnown.Infrastructure.Nexus;

namespace ModificationsKnown.Tests;

public sealed class NexusAccountStatusTests
{
    [Fact]
    public async Task ReadsPremiumAndSupporterFromValidateResponse()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"user_id\":42,\"name\":\"Tester\",\"is_premium\":true,\"is_supporter\":false,\"email\":\"x@example.com\",\"profile_url\":\"https://example.com\"}",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.TryAddWithoutValidation("x-rl-hourly-remaining", "88");
            response.Headers.TryAddWithoutValidation("x-rl-daily-remaining", "1200");
            return response;
        });

        using var http = new HttpClient(handler);
        using var client = new NexusModsClient(() => "test-key", null, http);

        var status = await client.GetAccountStatusAsync();

        Assert.Equal(42, status.UserId);
        Assert.True(status.IsPremium);
        Assert.False(status.IsSupporter);
        Assert.Equal(88, status.HourlyRemaining);
        Assert.Equal(1200, status.DailyRemaining);
        Assert.Equal("Premium", status.TierLabel);
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
