namespace ModificationsKnown.Core.Mods;

/// <summary>
/// A BepInDependency declared by a plugin. Version strings are intentionally
/// kept as text; BepInEx does not enforce SemVer, so resolution stays advisory.
/// </summary>
public sealed record ModDependency(
    string Guid,
    string? MinimumVersion = null,
    bool IsHardDependency = true);
