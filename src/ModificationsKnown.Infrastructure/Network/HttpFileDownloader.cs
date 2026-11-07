using System.Net;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Network;

namespace ModificationsKnown.Infrastructure.Network;

public sealed class HttpFileDownloader : IFileDownloader, IDisposable
{
    private const long MaximumDownloadBytes = 4L * 1024 * 1024 * 1024;

    private readonly HttpClient _httpClient;

    public HttpFileDownloader(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ModificationsKnown/0.1.0");
    }

    public async Task<OperationResult> DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return OperationResult.Fail("下载地址不是有效的 HTTP(S) URL。");
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (directory is not null) Directory.CreateDirectory(directory);

        var temporaryPath = destinationPath + ".part";

        try
        {
            using var response = await _httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return OperationResult.Fail($"下载失败：HTTP {(int)response.StatusCode}");

            var total = response.Content.Headers.ContentLength;
            if (total is > MaximumDownloadBytes)
                return OperationResult.Fail("文件超过 4 GB 上限，已拒绝下载。");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);

            var buffer = new byte[128 * 1024];
            long written = 0;

            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;

                written += read;
                if (written > MaximumDownloadBytes)
                    return OperationResult.Fail("文件超过 4 GB 上限，已中止下载。");

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                if (total is > 0)
                    progress?.Report(written / (double)total.Value * 100);
            }

            await output.FlushAsync(cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: true);
            progress?.Report(100);
            return OperationResult.Ok($"已下载 {written / 1024.0 / 1024.0:F1} MB。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return OperationResult.Fail($"下载失败：{exception.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
