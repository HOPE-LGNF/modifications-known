using System.Net;
using System.Text;
using ModificationsKnown.Infrastructure.Network;

namespace ModificationsKnown.Tests;

public sealed class HttpFileDownloaderTests
{
    [Fact]
    public async Task DownloadsToFinalPathAndLeavesNoPartFile()
    {
        using var temp = new TempDirectory();
        var destination = Path.Combine(temp.Path, "download.zip");
        var payload = Encoding.UTF8.GetBytes("archive bytes");

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        using var http = new HttpClient(handler);
        using var downloader = new HttpFileDownloader(httpClient: http);

        var result = await downloader.DownloadAsync("https://cdn.example.test/file.zip", destination);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(destination));
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
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
