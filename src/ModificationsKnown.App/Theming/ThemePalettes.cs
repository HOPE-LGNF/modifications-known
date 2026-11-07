using Avalonia.Media;

namespace ModificationsKnown.App.Theming;

public sealed record ThemePalette(
    string Id,
    string Name,
    string Description,
    string Background,
    string Sidebar,
    string Surface,
    string SurfaceAlt,
    string Border,
    string TextPrimary,
    string TextSecondary,
    string Accent,
    string AccentHover,
    string AccentSoft,
    string Success,
    string Warning,
    string Danger,
    string HeroStart,
    string HeroEnd)
{
    public Color AccentColor => Color.Parse(Accent);
}

public static class ThemePalettes
{
    public static IReadOnlyList<ThemePalette> All { get; } =
    [
        new(
            "moss",
            "苔原",
            "深绿与暖灰，安静、耐看，适合长时间整理模组。",
            "#0F1412",
            "#111815",
            "#151D19",
            "#1B2621",
            "#2A3730",
            "#E8F0EB",
            "#93A79D",
            "#79C99E",
            "#93DDB4",
            "#1E3A2C",
            "#7DCE9A",
            "#E5B567",
            "#E07A7A",
            "#1B3A2B",
            "#274D38"),
        new(
            "ember",
            "余烬",
            "深褐与琥珀色，像一间开着台灯的工作室。",
            "#14100E",
            "#191310",
            "#201815",
            "#2A211C",
            "#3B2D25",
            "#F3EBE4",
            "#B5A49A",
            "#E3A06B",
            "#F0B985",
            "#44291B",
            "#9BD19B",
            "#E7B86B",
            "#E07A7A",
            "#4A2A1A",
            "#6B3B24"),
        new(
            "slate",
            "夜航",
            "冷蓝与灰白，信息密度高时依然清晰。",
            "#0D1117",
            "#11161D",
            "#151C24",
            "#1C2530",
            "#2A3542",
            "#E6EDF3",
            "#9AA8B6",
            "#78A9E3",
            "#94BCEB",
            "#1D3049",
            "#7BCB9A",
            "#E5B567",
            "#E07A7A",
            "#182D47",
            "#24476C")
    ];

    public static ThemePalette Get(string? id) =>
        All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
