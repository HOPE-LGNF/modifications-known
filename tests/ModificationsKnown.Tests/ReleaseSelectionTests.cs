using ModificationsKnown.Core.Updates;

namespace ModificationsKnown.Tests;

public sealed class ReleaseSelectionTests
{
    private static ReleaseInfo Release(string tag, bool prerelease, bool draft = false) =>
        new(tag, draft, prerelease, $"https://github.com/HOPE-LGNF/modifications-known/releases/tag/{tag}",
            [new ReleaseAsset($"ModificationsKnown-{tag}-win-x64.exe", "sha256:" + new string('a', 64),
                $"https://github.com/HOPE-LGNF/modifications-known/releases/download/{tag}/ModificationsKnown-{tag}-win-x64.exe")]);

    private static LauncherVersion V(string text) =>
        LauncherVersion.TryParse(text, out var v) ? v : throw new InvalidOperationException(text);

    [Fact]
    public void StableIgnoresPrereleases()
    {
        var selection = ReleaseSelector.Select(
            AppUpdateChannel.Stable,
            V("0.6.0"),
            [Release("v0.7.0-beta", prerelease: true), Release("v0.6.1", prerelease: false)]);

        Assert.Equal(UpdateOutcome.UpdateAvailable, selection.Outcome);
        Assert.Equal("v0.6.1", selection.CandidateTag);
    }

    [Fact]
    public void PreviewTakesTheHighestPrerelease()
    {
        var selection = ReleaseSelector.Select(
            AppUpdateChannel.Preview,
            V("0.6.0"),
            [Release("v0.7.0-beta", prerelease: true), Release("v0.6.1", prerelease: false)]);

        Assert.Equal(UpdateOutcome.UpdateAvailable, selection.Outcome);
        Assert.Equal("v0.7.0-beta", selection.CandidateTag);
    }

    [Fact]
    public void PreviewPrefersHigherStableOverLowerPrerelease()
    {
        var selection = ReleaseSelector.Select(
            AppUpdateChannel.Preview,
            V("0.6.0"),
            [Release("v0.7.0-beta", prerelease: true), Release("v0.8.0", prerelease: false)]);

        Assert.Equal("v0.8.0", selection.CandidateTag);
    }

    [Fact]
    public void StableDoesNotDowngradeABetaInstall()
    {
        var selection = ReleaseSelector.Select(
            AppUpdateChannel.Stable,
            V("0.8.0-beta.2"),
            [Release("v0.7.0", prerelease: false)]);

        Assert.Equal(UpdateOutcome.UpToDate, selection.Outcome);
        Assert.Null(selection.CandidateTag);
    }

    [Fact]
    public void SameVersionIsUpToDate()
    {
        var selection = ReleaseSelector.Select(
            AppUpdateChannel.Stable,
            V("0.6.1"),
            [Release("v0.6.1", prerelease: false)]);

        Assert.Equal(UpdateOutcome.UpToDate, selection.Outcome);
    }

    [Fact]
    public void DraftsAreSkipped()
    {
        var selection = ReleaseSelector.Select(
            AppUpdateChannel.Preview,
            V("0.6.0"),
            [Release("v0.8.0-beta", prerelease: true, draft: true), Release("v0.7.0", prerelease: false)]);

        Assert.Equal("v0.7.0", selection.CandidateTag);
        Assert.Contains(selection.Diagnostics, line => line.Contains("draft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MarkerMismatchIsSkippedForTheChannel()
    {
        // A stable-looking tag marked as prerelease must not reach Stable.
        var selection = ReleaseSelector.Select(
            AppUpdateChannel.Stable,
            V("0.6.0"),
            [Release("v0.9.0", prerelease: true), Release("v0.6.1", prerelease: false)]);

        Assert.Equal("v0.6.1", selection.CandidateTag);

        // A prerelease-looking tag marked as a full release must not reach Preview either.
        var preview = ReleaseSelector.Select(
            AppUpdateChannel.Preview,
            V("0.6.0"),
            [Release("v0.9.0-beta", prerelease: false), Release("v0.7.0", prerelease: false)]);

        Assert.Equal("v0.7.0", preview.CandidateTag);
        Assert.Contains(preview.Diagnostics, line => line.Contains("0.9.0-beta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EqualVersionsKeepTheFirstCandidateAndRecordIt()
    {
        var first = Release("v0.7.0", prerelease: false);
        var second = Release("v0.7.0+meta", prerelease: false);

        var selection = ReleaseSelector.Select(AppUpdateChannel.Stable, V("0.6.0"), [first, second]);

        Assert.Same(first, selection.CandidateRelease);
        Assert.Contains(selection.Diagnostics, line => line.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidTagsAreSkipped()
    {
        var selection = ReleaseSelector.Select(
            AppUpdateChannel.Preview,
            V("0.6.0"),
            [Release("nightly", prerelease: true), Release("v0.7.0", prerelease: false)]);

        Assert.Equal("v0.7.0", selection.CandidateTag);
        Assert.Contains(selection.Diagnostics, line => line.Contains("nightly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoEligibleReleaseWhenEveryEntryIsRejected()
    {
        var selection = ReleaseSelector.Select(
            AppUpdateChannel.Preview,
            V("0.6.0"),
            [Release("nightly", prerelease: true), Release("v0.9.0", prerelease: true)]);

        Assert.Equal(UpdateOutcome.NoEligibleRelease, selection.Outcome);
        Assert.Null(selection.CandidateTag);
        Assert.NotEmpty(selection.Diagnostics);
    }

    [Fact]
    public void SelectionReportsChannelAndCurrentVersion()
    {
        var selection = ReleaseSelector.Select(
            AppUpdateChannel.Preview,
            V("0.6.0"),
            [Release("v0.7.0-beta", prerelease: true)]);

        Assert.Equal(AppUpdateChannel.Preview, selection.Channel);
        Assert.Equal("0.6.0", selection.CurrentVersion);
    }
}
