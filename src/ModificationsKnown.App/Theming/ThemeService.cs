using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace ModificationsKnown.App.Theming;

public static class ThemeService
{
    public static IReadOnlyList<ThemePalette> Palettes => ThemePalettes.All;

    public static ThemePalette Current { get; private set; } = ThemePalettes.All[0];

    public static void Apply(string? themeId)
    {
        var palette = ThemePalettes.Get(themeId);
        Current = palette;
        Apply(palette);
    }

    public static void Apply(ThemePalette palette)
    {
        Current = palette;
        if (Application.Current is null) return;

        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

        var resources = Application.Current.Resources;
        resources["AppBackgroundBrush"] = Brush(palette.Background);
        resources["SidebarBrush"] = Brush(palette.Sidebar);
        resources["SurfaceBrush"] = Brush(palette.Surface);
        resources["SurfaceAltBrush"] = Brush(palette.SurfaceAlt);
        resources["BorderBrush"] = Brush(palette.Border);
        resources["TextPrimaryBrush"] = Brush(palette.TextPrimary);
        resources["TextSecondaryBrush"] = Brush(palette.TextSecondary);
        resources["AccentBrush"] = Brush(palette.Accent);
        resources["AccentHoverBrush"] = Brush(palette.AccentHover);
        resources["AccentSoftBrush"] = Brush(palette.AccentSoft);
        resources["SuccessBrush"] = Brush(palette.Success);
        resources["WarningBrush"] = Brush(palette.Warning);
        resources["DangerBrush"] = Brush(palette.Danger);
        resources["HeroStartBrush"] = Brush(palette.HeroStart);
        resources["HeroEndBrush"] = Brush(palette.HeroEnd);
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
