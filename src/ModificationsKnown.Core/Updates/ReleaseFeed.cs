namespace ModificationsKnown.Core.Updates;

/// <summary>
/// One channel's worth of raw GitHub releases. <see cref="IsComplete"/> is
/// false when paging, limits or transport failed: an incomplete list must never
/// be presented as "the latest".
/// </summary>
public sealed record ReleaseFeedResult(
    bool IsComplete,
    IReadOnlyList<ReleaseInfo> Releases,
    IReadOnlyList<string> Diagnostics,
    string? Error)
{
    public static ReleaseFeedResult Complete(
        IReadOnlyList<ReleaseInfo> releases,
        IReadOnlyList<string>? diagnostics = null) =>
        new(true, releases, diagnostics ?? Array.Empty<string>(), null);

    public static ReleaseFeedResult Failed(string error, IReadOnlyList<string>? diagnostics = null) =>
        new(false, Array.Empty<ReleaseInfo>(), diagnostics ?? Array.Empty<string>(), error);
}

/// <summary>
/// Reads the launcher's own release feed. Implementations must use the fixed
/// repository, treat rate limits and transport errors as failures, and never
/// invent a candidate from a partial page list.
/// </summary>
public interface IAppReleaseFeed
{
    Task<ReleaseFeedResult> GetAsync(
        AppUpdateChannel channel,
        CancellationToken cancellationToken = default);
}
