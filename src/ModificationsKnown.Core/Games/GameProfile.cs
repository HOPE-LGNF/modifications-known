namespace ModificationsKnown.Core.Games;

/// <summary>
/// Static information about one edition of the supported game. Runtime paths
/// belong to <see cref="GameInstallation"/>, not here.
/// </summary>
public sealed record GameProfile(
    string Id,
    string DisplayName,
    string ShortName,
    GameEdition Edition,
    string SteamAppId,
    string NexusDomain,
    string DataDirectoryName,
    string? ExecutableName = null)
{
    public bool IsDemo => Edition == GameEdition.Demo;

    public string EditionLabel => Edition switch
    {
        GameEdition.Demo => "试玩版",
        GameEdition.Full => "正式版",
        _ => "未知版本"
    };
}

public static class KnownGameProfiles
{
    public static readonly GameProfile Full = new(
        "casualties-unknown",
        "未知伤亡",
        "CU",
        GameEdition.Full,
        "4576490",
        "scavprototype",
        "CasualtiesUnknown_Data");

    public static readonly GameProfile Demo = new(
        "casualties-unknown-demo",
        "未知伤亡（试玩版）",
        "CU Demo",
        GameEdition.Demo,
        "4576510",
        "scavprototype",
        "CasualtiesUnknown_Data");

    public static IReadOnlyList<GameProfile> All { get; } = [Full, Demo];

    public static GameProfile? FindByAppId(string appId) =>
        All.FirstOrDefault(profile => string.Equals(profile.SteamAppId, appId, StringComparison.Ordinal));

    public static GameProfile? FindByEdition(GameEdition edition) =>
        All.FirstOrDefault(profile => profile.Edition == edition);
}
