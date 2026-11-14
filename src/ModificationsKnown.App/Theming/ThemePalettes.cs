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
    string AccentContrast,
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
            "daylight",
            "日光",
            "浅色白卡与柔和绿，适合白天长时间整理模组。",
            "#EEF1EF",
            "#F5F7F6",
            "#FFFFFF",
            "#F7F9F8",
            "#DFE6E2",
            "#1E2A24",
            "#6D7F76",
            "#3F9D6F",
            "#4FB37F",
            "#E5F3EC",
            "#FFFFFF",
            "#2F8F5B",
            "#B57A25",
            "#C25B5B",
            "#E8F4ED",
            "#DDF0E6"),
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
            "#0F1412",
            "#7DCE9A",
            "#E5B567",
            "#E07A7A",
            "#1B3A2B",
            "#274D38"),
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
            "#0D1117",
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
