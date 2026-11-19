namespace ModificationsKnown.Core.Updates;

/// <summary>Immutable snapshot of what a check produced. Installing must be
/// validated against this snapshot, not against whatever the UI still shows.</summary>
public sealed record UpdateCandidate(
    long Generation,
    AppUpdateChannel Channel,
    LauncherVersion Version,
    string Tag,
    ReleaseInfo Release,
    string? AssetUrl,
    string? AssetSha256,
    string? ReleaseUrl,
    string? InstallNote);

public enum UpdateCheckState
{
    Completed,
    Superseded,
    Busy
}

public sealed record UpdateCheckResult(
    UpdateCheckState State,
    UpdateOutcome Outcome,
    AppUpdateChannel Channel,
    string CurrentVersion,
    UpdateCandidate? Candidate,
    string? Error);

/// <summary>
/// Owns the launcher update state machine: which channel is saved, the current
/// candidate snapshot, a generation counter that discards late responses, and
/// the check/install mutex. UI text and file replacement stay outside.
/// </summary>
public sealed class AppUpdateCoordinator
{
    private readonly IAppReleaseFeed _feed;
    private readonly Func<string> _currentVersion;
    private readonly object _gate = new();

    private long _generation;
    private AppUpdateChannel _savedChannel = AppUpdateChannel.Stable;

    public AppUpdateCoordinator(IAppReleaseFeed feed, Func<string> currentVersion)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
    }

    public AppUpdateChannel SavedChannel
    {
        get
        {
            lock (_gate) return _savedChannel;
        }
    }

    public bool IsChecking { get; private set; }

    public bool IsInstalling { get; private set; }

    public UpdateCandidate? Candidate { get; private set; }

    public long Generation
    {
        get
        {
            lock (_gate) return _generation;
        }
    }

    /// <summary>Records the persisted channel. Any change invalidates the
    /// candidate and makes in-flight checks unable to write back. Returns true
    /// when the channel actually changed.</summary>
    public bool SetSavedChannel(AppUpdateChannel channel)
    {
        lock (_gate)
        {
            if (_savedChannel == channel) return false;

            _savedChannel = channel;
            _generation++;
            Candidate = null;
            return true;
        }
    }

    /// <summary>Clears the candidate on demand (failed download handled by the
    /// caller), without touching the saved channel.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            Candidate = null;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        AppUpdateChannel channel;

        lock (_gate)
        {
            if (IsInstalling)
            {
                return new UpdateCheckResult(
                    UpdateCheckState.Busy,
                    UpdateOutcome.Failed,
                    _savedChannel,
                    _currentVersion(),
                    Candidate,
                    "正在安装更新，完成后再检查。");
            }

            generation = ++_generation;
            channel = _savedChannel;
            IsChecking = true;
            Candidate = null;
        }

        try
        {
            var currentText = _currentVersion();
            if (!LauncherVersion.TryParse(currentText, out var current))
            {
                return Complete(UpdateOutcome.Failed, channel, currentText, null, "无法识别当前程序版本。");
            }

            var feed = await _feed.GetAsync(channel, cancellationToken);

            lock (_gate)
            {
                if (generation != _generation)
                {
                    return new UpdateCheckResult(
                        UpdateCheckState.Superseded, UpdateOutcome.Failed, channel, currentText, null, null);
                }
            }

            if (!feed.IsComplete || feed.Error is not null)
            {
                return Complete(UpdateOutcome.Failed, channel, currentText, null, feed.Error ?? "检查更新失败。");
            }

            var selection = ReleaseSelector.Select(channel, current, feed.Releases);

            if (selection.Outcome == UpdateOutcome.UpdateAvailable && selection.CandidateRelease is { } release)
            {
                string? assetUrl = null;
                string? assetSha256 = null;
                string? note = null;

                if (ReleaseAssetPolicy.TrySelectInstallable(release.Assets, out var asset, out var reason)
                    && asset is not null
                    && ReleaseAssetPolicy.TryGetSha256(asset.Digest, out var sha256))
                {
                    assetUrl = asset.DownloadUrl;
                    assetSha256 = sha256;
                }
                else
                {
                    note = reason;
                }

                var candidate = new UpdateCandidate(
                    generation,
                    channel,
                    selection.Candidate!.Value,
                    release.Tag,
                    release,
                    assetUrl,
                    assetSha256,
                    ReleaseAssetPolicy.IsTrustedReleaseUrl(release.HtmlUrl) ? release.HtmlUrl : null,
                    note);

                lock (_gate)
                {
                    if (generation != _generation) return Superseded(channel, currentText);
                    Candidate = candidate;
                }

                return new UpdateCheckResult(
                    UpdateCheckState.Completed,
                    UpdateOutcome.UpdateAvailable,
                    channel,
                    currentText,
                    candidate,
                    null);
            }

            return Complete(selection.Outcome, channel, currentText, null, null);
        }
        finally
        {
            lock (_gate) IsChecking = false;
        }
    }

    /// <summary>Claims the install slot for a specific candidate generation.
    /// Fails when the candidate is gone, superseded, or another install runs.</summary>
    public bool TryBeginInstall(long expectedGeneration)
    {
        lock (_gate)
        {
            if (IsInstalling) return false;
            if (Candidate is null) return false;
            if (Candidate.Generation != expectedGeneration) return false;
            if (expectedGeneration != _generation) return false;

            IsInstalling = true;
            return true;
        }
    }

    /// <summary>Releases the install slot. <paramref name="keepCandidate"/> true
    /// after a failed download so the user can retry.</summary>
    public void EndInstall(bool keepCandidate)
    {
        lock (_gate)
        {
            IsInstalling = false;
            if (!keepCandidate)
            {
                Candidate = null;
                _generation++;
            }
        }
    }

    private UpdateCheckResult Complete(
        UpdateOutcome outcome,
        AppUpdateChannel channel,
        string currentText,
        UpdateCandidate? candidate,
        string? error) =>
        new(UpdateCheckState.Completed, outcome, channel, currentText, candidate, error);

    private static UpdateCheckResult Superseded(AppUpdateChannel channel, string currentText) =>
        new(UpdateCheckState.Superseded, UpdateOutcome.Failed, channel, currentText, null, null);
}
