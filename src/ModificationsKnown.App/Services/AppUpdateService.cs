using System.Net.Http;
using System.Reflection;
using ModificationsKnown.Core.Updates;
using ModificationsKnown.Infrastructure.Updates;

namespace ModificationsKnown.App.Services;

public sealed record AppUpdateCheck(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string? AssetUrl,
    string? AssetSha256,
    string Message,
    UpdateOutcome Outcome = UpdateOutcome.Failed,
    AppUpdateChannel Channel = AppUpdateChannel.Stable);

/// <summary>
/// Application-level orchestration of the launcher's own update check: read the
/// installed version, ask the fixed GitHub feed for the selected channel, run
/// the shared channel policy and decide whether a one-click install is allowed.
/// Network and JSON parsing live in <see cref="GitHubAppReleaseFeed"/>.
/// </summary>
public sealed class AppUpdateService
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly IAppReleaseFeed _feed;
    private readonly Func<string> _currentVersion;

    public AppUpdateService()
        : this(new GitHubAppReleaseFeed(SharedHttp), ReadInstalledVersion)
    {
    }

    public AppUpdateService(IAppReleaseFeed feed, Func<string> currentVersion)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
    }

    public string CurrentVersion => _currentVersion();

    public async Task<AppUpdateCheck> CheckAsync(
        AppUpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        var currentText = CurrentVersion;

        if (!LauncherVersion.TryParse(currentText, out var current))
        {
            return Failed(
                channel,
                currentText,
                "无法识别当前程序版本，请手动打开 Release 页查看。");
        }

        var feed = await _feed.GetAsync(channel, cancellationToken);

        if (!feed.IsComplete || feed.Error is not null)
        {
            return Failed(channel, currentText, feed.Error ?? "检查更新失败。");
        }

        var selection = ReleaseSelector.Select(channel, current, feed.Releases);

        switch (selection.Outcome)
        {
            case UpdateOutcome.UpdateAvailable when selection.Candidate is { } candidate:
            {
                var release = selection.CandidateRelease!;
                string? assetUrl = null;
                string? assetSha256 = null;
                var note = string.Empty;

                if (ReleaseAssetPolicy.TrySelectInstallable(release.Assets, out var asset, out var reason)
                    && asset is not null
                    && ReleaseAssetPolicy.TryGetSha256(asset.Digest, out var sha256))
                {
                    assetUrl = asset.DownloadUrl;
                    assetSha256 = sha256;
                }
                else
                {
                    note = " " + reason;
                }

                var releaseUrl = ReleaseAssetPolicy.IsTrustedReleaseUrl(release.HtmlUrl)
                    ? release.HtmlUrl
                    : string.Empty;

                return new AppUpdateCheck(
                    true,
                    currentText,
                    candidate.ToString(),
                    releaseUrl,
                    assetUrl,
                    assetSha256,
                    $"{ChannelLabel(channel)}发现新版本 v{candidate}（当前 v{currentText}）。{note}".Trim(),
                    UpdateOutcome.UpdateAvailable,
                    channel);
            }

            case UpdateOutcome.NoEligibleRelease:
                return new AppUpdateCheck(
                    false,
                    currentText,
                    currentText,
                    string.Empty,
                    null,
                    null,
                    $"{ChannelLabel(channel)}没有可用的发行版本。",
                    UpdateOutcome.NoEligibleRelease,
                    channel);

            default:
                return new AppUpdateCheck(
                    false,
                    currentText,
                    currentText,
                    string.Empty,
                    null,
                    null,
                    $"{ChannelLabel(channel)}已是最新版本（v{currentText}）。",
                    UpdateOutcome.UpToDate,
                    channel);
        }
    }

    private static AppUpdateCheck Failed(AppUpdateChannel channel, string currentVersion, string message) =>
        new(
            false,
            currentVersion,
            currentVersion,
            string.Empty,
            null,
            null,
            message,
            UpdateOutcome.Failed,
            channel);

    private static string ChannelLabel(AppUpdateChannel channel) =>
        channel == AppUpdateChannel.Preview ? "预览通道" : "稳定通道";

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
