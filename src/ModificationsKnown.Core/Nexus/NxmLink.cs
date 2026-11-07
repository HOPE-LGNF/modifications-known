namespace ModificationsKnown.Core.Nexus;

/// <summary>
/// A validated link produced by the Nexus “Mod manager download” button.
/// The key and expiry are short-lived download credentials and must never be
/// logged or persisted.
/// </summary>
public sealed record NxmLink(
    string GameDomain,
    long ModId,
    long FileId,
    string? Key,
    long? Expires,
    long? UserId = null)
{
    public bool HasKey => !string.IsNullOrWhiteSpace(Key) && Expires is not null;

    public bool IsExpired(DateTimeOffset now) =>
        Expires is { } expires && DateTimeOffset.FromUnixTimeSeconds(expires) <= now;
}
