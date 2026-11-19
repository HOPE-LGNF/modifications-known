using ModificationsKnown.Core.Updates;

namespace ModificationsKnown.Tests;

public sealed class AppUpdateCoordinatorTests
{
    private static ReleaseInfo Release(
        string tag,
        bool prerelease,
        string? digest = null,
        string? downloadUrl = null)
    {
        var assetName = $"ModificationsKnown-{tag}-win-x64.exe";
        return new ReleaseInfo(
            tag,
            false,
            prerelease,
            $"https://github.com/HOPE-LGNF/modifications-known/releases/tag/{tag}",
            [
                new ReleaseAsset(
                    assetName,
                    digest ?? "sha256:" + new string('a', 64),
                    downloadUrl ?? $"https://github.com/HOPE-LGNF/modifications-known/releases/download/{tag}/{assetName}")
            ],
            7);
    }

    private static AppUpdateCoordinator Coordinator(StubFeed feed, string current = "0.6.0") =>
        new(feed, () => current);

    [Fact]
    public async Task SuccessfulCheckStoresACandidate()
    {
        var feed = new StubFeed { Result = ReleaseFeedResult.Complete([Release("v0.7.0", prerelease: false)]) };
        var coordinator = Coordinator(feed);

        var result = await coordinator.CheckAsync();

        Assert.Equal(UpdateCheckState.Completed, result.State);
        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.NotNull(coordinator.Candidate);
        Assert.Equal("v0.7.0", coordinator.Candidate!.Tag);
        Assert.NotNull(coordinator.Candidate.AssetUrl);
    }

    [Fact]
    public async Task SavingADifferentChannelDropsTheCandidate()
    {
        var feed = new StubFeed { Result = ReleaseFeedResult.Complete([Release("v0.7.0", prerelease: false)]) };
        var coordinator = Coordinator(feed);
        await coordinator.CheckAsync();
        Assert.NotNull(coordinator.Candidate);

        var changed = coordinator.SetSavedChannel(AppUpdateChannel.Preview);

        Assert.True(changed);
        Assert.Null(coordinator.Candidate);
    }

    [Fact]
    public async Task SavingTheSameChannelKeepsTheCandidate()
    {
        var feed = new StubFeed { Result = ReleaseFeedResult.Complete([Release("v0.7.0", prerelease: false)]) };
        var coordinator = Coordinator(feed);
        await coordinator.CheckAsync();

        Assert.False(coordinator.SetSavedChannel(AppUpdateChannel.Stable));
        Assert.NotNull(coordinator.Candidate);
    }

    [Fact]
    public async Task LateResponseIsDiscardedAfterAChannelChange()
    {
        var pending = new TaskCompletionSource<ReleaseFeedResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new StubFeed { Pending = pending };
        var coordinator = Coordinator(feed);

        var check = coordinator.CheckAsync();
        coordinator.SetSavedChannel(AppUpdateChannel.Preview);
        pending.SetResult(ReleaseFeedResult.Complete([Release("v0.7.0", prerelease: false)]));

        var result = await check;

        Assert.Equal(UpdateCheckState.Superseded, result.State);
        Assert.Null(coordinator.Candidate);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task CheckIsRefusedWhileInstalling()
    {
        var feed = new StubFeed { Result = ReleaseFeedResult.Complete([Release("v0.7.0", prerelease: false)]) };
        var coordinator = Coordinator(feed);
        var first = await coordinator.CheckAsync();

        Assert.True(coordinator.TryBeginInstall(first.Candidate!.Generation));

        var second = await coordinator.CheckAsync();

        Assert.Equal(UpdateCheckState.Busy, second.State);
        Assert.Equal(1, feed.Calls);
    }

    [Fact]
    public async Task StaleGenerationCannotStartAnInstall()
    {
        var feed = new StubFeed { Result = ReleaseFeedResult.Complete([Release("v0.7.0", prerelease: false)]) };
        var coordinator = Coordinator(feed);
        var check = await coordinator.CheckAsync();
        var generation = check.Candidate!.Generation;

        coordinator.Invalidate();

        Assert.False(coordinator.TryBeginInstall(generation));
        Assert.False(coordinator.IsInstalling);
    }

    [Fact]
    public async Task CurrentCandidateCanStartAnInstallExactlyOnce()
    {
        var feed = new StubFeed { Result = ReleaseFeedResult.Complete([Release("v0.7.0", prerelease: false)]) };
        var coordinator = Coordinator(feed);
        var check = await coordinator.CheckAsync();
        var generation = check.Candidate!.Generation;

        Assert.True(coordinator.TryBeginInstall(generation));
        Assert.False(coordinator.TryBeginInstall(generation));

        coordinator.EndInstall(keepCandidate: true);

        Assert.False(coordinator.IsInstalling);
        Assert.NotNull(coordinator.Candidate);
    }

    [Fact]
    public async Task FailedCheckClearsTheCandidate()
    {
        var feed = new StubFeed { Result = ReleaseFeedResult.Failed("GitHub API 限流") };
        var coordinator = Coordinator(feed);

        var result = await coordinator.CheckAsync();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Null(coordinator.Candidate);
        Assert.Contains("限流", result.Error);
    }

    [Fact]
    public async Task NoEligibleReleaseLeavesNoCandidate()
    {
        var feed = new StubFeed { Result = ReleaseFeedResult.Complete(Array.Empty<ReleaseInfo>()) };
        var coordinator = Coordinator(feed);

        var result = await coordinator.CheckAsync();

        Assert.Equal(UpdateOutcome.NoEligibleRelease, result.Outcome);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task CandidateWithoutValidDigestKeepsTheLinkButNoAutoInstall()
    {
        var feed = new StubFeed
        {
            Result = ReleaseFeedResult.Complete(
                [Release("v0.7.0", prerelease: false, digest: "sha256:bad")])
        };
        var coordinator = Coordinator(feed);

        var result = await coordinator.CheckAsync();

        Assert.NotNull(result.Candidate);
        Assert.Null(result.Candidate!.AssetUrl);
        Assert.Null(result.Candidate.AssetSha256);
        Assert.NotNull(result.Candidate.InstallNote);
        Assert.NotNull(result.Candidate.ReleaseUrl);
    }

    [Fact]
    public async Task UnparsableCurrentVersionFailsWithoutCallingTheFeed()
    {
        var feed = new StubFeed();
        var coordinator = new AppUpdateCoordinator(feed, () => "not-a-version");

        var result = await coordinator.CheckAsync();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(0, feed.Calls);
    }

    private sealed class StubFeed : IAppReleaseFeed
    {
        public ReleaseFeedResult Result { get; set; } =
            ReleaseFeedResult.Complete(Array.Empty<ReleaseInfo>());

        public TaskCompletionSource<ReleaseFeedResult>? Pending { get; set; }

        public int Calls { get; private set; }

        public Task<ReleaseFeedResult> GetAsync(
            AppUpdateChannel channel,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Pending is null ? Task.FromResult(Result) : Pending.Task;
        }
    }
}
