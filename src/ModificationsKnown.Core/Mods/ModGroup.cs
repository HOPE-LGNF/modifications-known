namespace ModificationsKnown.Core.Mods;

/// <summary>
/// A named snapshot of enabled package ids for one game instance.
/// Groups never cross game versions: the instance path is the storage key.
/// </summary>
public sealed record ModGroup(
    string Id,
    string Name,
    IReadOnlyList<string> EnabledPackageIds,
    DateTimeOffset CreatedAt);
