namespace ModificationsKnown.Core.Games;

/// <summary>
/// A detected local installation. The launcher can hold both the full game and
/// the demo at once; UI code must not assume a single installation.
/// </summary>
public sealed record GameInstallation(
    GameProfile Profile,
    string GameDirectory,
    string? ExecutablePath,
    bool HasBepInEx,
    bool HasPluginsDirectory)
{
    public string Alias { get; init; } = string.Empty;

    public string BepInExDirectory => Path.Combine(GameDirectory, "BepInEx");

    public string PluginsDirectory => Path.Combine(GameDirectory, "BepInEx", "plugins");

    public string ConfigDirectory => Path.Combine(GameDirectory, "BepInEx", "config");

    public string DisplayPath => GameDirectory;

    public string DisplayLabel => string.IsNullOrWhiteSpace(Alias)
        ? $"{Profile.DisplayName} · {Profile.EditionLabel}"
        : Alias;

    public string BepInExLabel => HasBepInEx ? "BepInEx 已安装" : "BepInEx 未安装";

    public string LaunchTarget => ExecutablePath ?? GameDirectory;
}
