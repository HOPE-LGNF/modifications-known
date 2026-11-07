using ModificationsKnown.Core.Diagnostics;

namespace ModificationsKnown.App.Services;

/// <summary>
/// Lightweight cache for remote thumbnails. The UI layer owns this because it
/// decodes Avalonia bitmaps; the cache file itself is just the original image.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly IAppLogger _logger;

    public ThumbnailService(IAppLogger logger)
    {
        _logger = logger;
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ModificationsKnown",
            "cache",
            "thumbnails");
        Directory.CreateDirectory(_cacheDirectory);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ModificationsKnown/0.1.0");
    }

    public async Task<Avalonia.Media.Imaging.Bitmap?> LoadAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return null;

        var cachePath = Path.Combine(_cacheDirectory, Hash(uri.AbsoluteUri) + ".img");

        try
        {
            if (!File.Exists(cachePath))
            {
                var bytes = await _httpClient.GetByteArrayAsync(uri, cancellationToken);
                if (bytes.Length is 0 or > 12 * 1024 * 1024) return null;
                await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
            }

            return new Avalonia.Media.Imaging.Bitmap(cachePath);
        }
        catch (Exception exception)
        {
            _logger.Warn($"缩略图加载失败：{exception.Message}");
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }
}
