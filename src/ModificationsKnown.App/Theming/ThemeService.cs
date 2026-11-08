using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Theming;

public static class ThemeService
{
    private static readonly List<ThemePalette> Themes = new(ThemePalettes.All);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<ThemePalette> Palettes => Themes;

    public static string ThemesDirectory => Path.Combine(AppPaths.RootDirectory, "themes");

    public static ThemePalette Current { get; private set; } = ThemePalettes.All[0];

    public static void LoadCustomThemes(IAppLogger? logger = null)
    {
        try
        {
            Directory.CreateDirectory(ThemesDirectory);
            foreach (var file in Directory.EnumerateFiles(ThemesDirectory, "*.json"))
            {
                if (!TryReadTheme(file, out var palette, out var error, logger))
                    continue;
                if (palette is null) continue;
                if (Themes.Any(theme => string.Equals(theme.Id, palette.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Themes.Add(palette);
                logger?.Info($"Loaded custom theme: {palette.Name} ({palette.Id})");
            }
        }
        catch (Exception exception)
        {
            logger?.Warn($"加载自定义主题失败：{exception.Message}");
        }
    }

    public static bool TryImportTheme(
        string sourcePath,
        out ThemePalette? palette,
        out string error,
        IAppLogger? logger = null)
    {
        palette = null;
        if (!TryReadTheme(sourcePath, out palette, out error, logger))
            return false;
        if (palette is null) return false;

        var imported = palette;
        if (ThemePalettes.All.Any(theme => string.Equals(theme.Id, imported.Id, StringComparison.OrdinalIgnoreCase)))
        {
            error = "主题 ID 与内置主题冲突，请换一个 id。";
            return false;
        }

        try
        {
            Directory.CreateDirectory(ThemesDirectory);
            var destination = Path.Combine(ThemesDirectory, $"{imported.Id}.json");
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, destination, overwrite: true);

            var existing = Themes.FindIndex(theme => string.Equals(theme.Id, imported.Id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) Themes[existing] = imported;
            else Themes.Add(imported);

            logger?.Info($"Imported custom theme: {imported.Name} ({imported.Id})");
            palette = imported;
            return true;
        }
        catch (Exception exception)
        {
            error = $"保存主题失败：{exception.Message}";
            logger?.Warn(error);
            return false;
        }
    }

    public static void Apply(string? themeId)
    {
        var palette = Themes.FirstOrDefault(theme => string.Equals(theme.Id, themeId, StringComparison.OrdinalIgnoreCase))
                      ?? ThemePalettes.Get(themeId);
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
        resources["AccentContrastBrush"] = Brush(palette.AccentContrast);
        resources["ScrimBrush"] = new SolidColorBrush(Color.Parse("#CC080B09"));
        resources["SuccessBrush"] = Brush(palette.Success);
        resources["WarningBrush"] = Brush(palette.Warning);
        resources["DangerBrush"] = Brush(palette.Danger);
        resources["HeroStartBrush"] = Brush(palette.HeroStart);
        resources["HeroEndBrush"] = Brush(palette.HeroEnd);

        // Avalonia's FluentTheme reads the platform accent color for checks,
        // sliders and selection. Without overriding these, a Windows accent
        // (e.g. #604B27) leaks into an otherwise green MK theme.
        var accent = Color.Parse(palette.Accent);
        var accentHover = Color.Parse(palette.AccentHover);
        var accentSoft = Color.Parse(palette.AccentSoft);
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = accentHover;
        resources["SystemAccentColorLight2"] = accentHover;
        resources["SystemAccentColorLight3"] = accentHover;
        resources["SystemAccentColorDark1"] = accentSoft;
        resources["SystemAccentColorDark2"] = accentSoft;
        resources["SystemAccentColorDark3"] = accentSoft;
        resources["SystemControlHighlightAccentBrush"] = Brush(palette.Accent);
        resources["SystemControlForegroundAccentBrush"] = Brush(palette.Accent);
        resources["SystemControlBackgroundAccentBrush"] = Brush(palette.Accent);
        resources["ProgressBarForeground"] = Brush(palette.Accent);
        resources["CheckBoxCheckBackgroundFillChecked"] = Brush(palette.Accent);
        resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = Brush(palette.AccentHover);
        resources["CheckBoxCheckBackgroundFillCheckedPressed"] = Brush(palette.AccentHover);
        resources["CheckBoxCheckBackgroundStrokeChecked"] = Brush(palette.Accent);
        resources["CheckBoxCheckGlyphForegroundChecked"] = Brush(palette.AccentContrast);
    }

    private static bool TryReadTheme(
        string path,
        out ThemePalette? palette,
        out string error,
        IAppLogger? logger)
    {
        palette = null;
        try
        {
            var json = File.ReadAllText(path);
            var candidate = JsonSerializer.Deserialize<ThemePalette>(json, JsonOptions);
            if (candidate is null)
            {
                error = "主题文件是空的。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(candidate.Id)
                || candidate.Id.Contains('/')
                || candidate.Id.Contains('\\')
                || candidate.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "主题 id 不能为空，也不能包含路径分隔符。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                error = "主题 name 不能为空。";
                return false;
            }

            foreach (var color in CandidateColors(candidate))
                Color.Parse(color);

            palette = candidate;
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"主题 JSON 无效：{exception.Message}";
            logger?.Warn(error);
            return false;
        }
    }

    private static IEnumerable<string> CandidateColors(ThemePalette theme) =>
    [
        theme.Background, theme.Sidebar, theme.Surface, theme.SurfaceAlt, theme.Border,
        theme.TextPrimary, theme.TextSecondary, theme.Accent, theme.AccentHover,
        theme.AccentSoft, theme.AccentContrast, theme.Success, theme.Warning,
        theme.Danger, theme.HeroStart, theme.HeroEnd
    ];

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
