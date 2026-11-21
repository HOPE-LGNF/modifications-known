namespace ModificationsKnown.Core.Updates;

public enum UpdateOutcome
{
    UpdateAvailable,
    UpToDate,
    NoEligibleRelease,
    Failed
}

public sealed record ReleaseAsset(string Name, string? Digest, string? DownloadUrl);

public sealed record ReleaseInfo(
    string Tag,
    bool Draft,
    bool Prerelease,
    string HtmlUrl,
    IReadOnlyList<ReleaseAsset> Assets,
    long Id = 0);

public sealed record UpdateSelection(
    UpdateOutcome Outcome,
    AppUpdateChannel Channel,
    string CurrentVersion,
    LauncherVersion? Candidate,
    string? CandidateTag,
    ReleaseInfo? CandidateRelease,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Pure channel policy: which release (if any) a channel may offer. Network
/// access, paging and asset eligibility live in the service layer.
///
/// Rules: drafts never count; a tag must parse as SemVer; the tag's prerelease
/// shape must agree with GitHub's prerelease flag (a stable-looking tag marked
/// as prerelease must not reach Stable, and vice versa); Stable ignores
/// prereleases entirely; Preview ranks every consistent release, stable ones
/// included. Equal versions keep the first entry and say so in diagnostics.
/// </summary>
public static class ReleaseSelector
{
    public static UpdateSelection Select(
        AppUpdateChannel channel,
        LauncherVersion current,
        IEnumerable<ReleaseInfo> releases)
    {
        var diagnostics = new List<string>();
        ReleaseInfo? best = null;
        LauncherVersion bestVersion = default;
        string? bestTag = null;

        foreach (var release in releases)
        {
            if (release.Draft)
            {
                diagnostics.Add($"{release.Tag}: skipped, draft release");
                continue;
            }

            if (!LauncherVersion.TryParse(release.Tag, out var version))
            {
                diagnostics.Add($"{release.Tag}: skipped, tag is not a semantic version");
                continue;
            }

            if (version.HasPrerelease != release.Prerelease)
            {
                diagnostics.Add(
                    $"{release.Tag}: skipped, tag prerelease shape ({version.HasPrerelease}) " +
                    $"disagrees with the release marker ({release.Prerelease})");
                continue;
            }

            if (channel == AppUpdateChannel.Stable && version.HasPrerelease)
            {
                diagnostics.Add($"{release.Tag}: skipped, prerelease is not offered on the stable channel");
                continue;
            }

            if (best is null)
            {
                best = release;
                bestVersion = version;
                bestTag = release.Tag;
                continue;
            }

            var comparison = version.CompareTo(bestVersion);
            if (comparison > 0)
            {
                best = release;
                bestVersion = version;
                bestTag = release.Tag;
            }
            else if (comparison == 0)
            {
                diagnostics.Add($"{release.Tag}: duplicate of {bestTag}, keeping the earlier entry");
            }
        }

        if (best is null)
        {
            return new UpdateSelection(
                UpdateOutcome.NoEligibleRelease,
                channel,
                current.ToString(),
                null,
                null,
                null,
                diagnostics);
        }

        if (bestVersion.CompareTo(current) > 0)
        {
            return new UpdateSelection(
                UpdateOutcome.UpdateAvailable,
                channel,
                current.ToString(),
                bestVersion,
                bestTag,
                best,
                diagnostics);
        }

        return new UpdateSelection(
            UpdateOutcome.UpToDate,
            channel,
            current.ToString(),
            null,
            null,
            null,
            diagnostics);
    }
}
