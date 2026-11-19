using System.Net.Http;
using System.Reflection;
using ModificationsKnown.Core.Updates;
using ModificationsKnown.Infrastructure.Updates;

namespace ModificationsKnown.App.Services;

/// <summary>
/// Application-level pieces of the launcher's own update flow: the installed
/// version identity, the downloader, and the coordinator factory. Channel
/// checking and candidate state live in <see cref="AppUpdateCoordinator"/>;
/// the GitHub feed lives in <see cref="GitHubAppReleaseFeed"/>.
/// </summary>
public sealed class AppUpdateService
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly Func<string> _currentVersion;

    public AppUpdateService()
        : this(ReadInstalledVersion)
    {
    }

    public AppUpdateService(Func<string> currentVersion) =>
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));

    public string CurrentVersion => _currentVersion();

    /// <summary>Builds the state machine on the same fixed feed and version
    /// source this service uses, so the UI has one source of truth.</summary>
    public AppUpdateCoordinator CreateCoordinator() =>
        new(new GitHubAppReleaseFeed(SharedHttp), () => CurrentVersion);

    /// <summary>Informational version of this application's assembly, which
    /// keeps the prerelease suffix a release build passes via /p:Version.</summary>
    private static string ReadInstalledVersion()
    {
        var assembly = typeof(AppUpdateService).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational)) return informational;

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public async Task<bool> DownloadAsync(
        string url,
        string destination,
        string? expectedSha256 = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SharedHttp.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;

            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(destination);
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                copied += read;
                if (total is > 0) progress?.Report(copied / (double)total.Value);
            }

            await output.FlushAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(destination); } catch { }
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
