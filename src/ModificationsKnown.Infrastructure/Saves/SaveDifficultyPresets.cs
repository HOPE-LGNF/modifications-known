using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// One shipped difficulty preset. <see cref="Overrides"/> lists only the run
/// settings the preset changes; the rest come from the normal baseline.
/// </summary>
public sealed class SaveDifficultyPreset
{
    internal SaveDifficultyPreset(
        string key,
        string label,
        IReadOnlyDictionary<string, SaveSettingValue>? overrides)
    {
        Key = key;
        Label = label;
        Overrides = overrides ?? new Dictionary<string, SaveSettingValue>();
        EffectiveSettings = SaveDifficultyPresets.Merge(SaveDifficultyPresets.NormalValues, Overrides);
    }

    public string Key { get; }

    public string Label { get; }

    /// <summary>Run settings that differ from normal.</summary>
    public IReadOnlyDictionary<string, SaveSettingValue> Overrides { get; }

    /// <summary>Baseline plus overrides: the complete table a save must match.</summary>
    public IReadOnlyDictionary<string, SaveSettingValue> EffectiveSettings { get; }
}

/// <summary>
/// Matches a save's <c>runSettings</c> against the game's six presets.
/// <para>
/// The table below is copied from the game assembly
/// (<c>RunSettings.settingTypes</c> / <c>RunSettings.presets</c>, read with
/// ilspycmd; game build shipped with 51 keys including
/// <c>allowlayerendmovement</c>). Values are the game's own: booleans, dropdown
/// indices and floats; labels use the official zh-CN wording
/// (<c>/other/preset*</c>), where the "normal" preset is called 艰难.
/// </para>
/// <para>
/// Matching is deliberately strict: the save must carry every known key and
/// nothing else. An unknown key (newer game version, mod) or a missing key
/// (older save format) yields "自定义" rather than a guessed preset.
/// </para>
/// </summary>
public static class SaveDifficultyPresets
{
    public const string CustomKey = "custom";

    /// <summary>Short chip label; see <see cref="CustomDescription"/> for the tooltip.</summary>
    public const string CustomLabel = "自定义";

    /// <summary>The game calls this “自定义游戏设置”.</summary>
    public const string CustomDescription = "自定义游戏设置";

    /// <summary>Float tolerance for the game's float32 round-trip.</summary>
    public const double Tolerance = 0.01;

    private static SaveSettingValue N(double value) => SaveSettingValue.FromNumber(value);

    private static SaveSettingValue B(bool value) => SaveSettingValue.FromBoolean(value);

    private static readonly Dictionary<string, SaveSettingValue> Baseline = new(StringComparer.Ordinal)
    {
        ["unchipped"] = B(false),
        ["endless"] = B(false),
        ["baselootdensity"] = N(1),
        ["lootmultiplier"] = N(1),
        ["basetrapdensity"] = N(1),
        ["trapincrease"] = N(0),
        ["ambientlight"] = N(0),
        ["timelimit"] = N(60),
        ["startingsupplies"] = N(1),
        ["enemystrength"] = N(1),
        ["xpgain"] = N(1),
        ["metabolismrate"] = N(1),
        ["healingrate"] = N(1),
        ["fracturepain"] = N(1),
        ["bleedrate"] = N(1),
        ["infectionspeed"] = N(1),
        ["infectionchance"] = N(1),
        ["fibrillationrate"] = N(1),
        ["moodnormalizationrate"] = N(1),
        ["bonuslimbarmor"] = N(0),
        ["staminaregen"] = N(1),
        ["attackdamage"] = N(1),
        ["minigamehandshake"] = N(1),
        ["sleepcyclespeed"] = N(1),
        ["encumbrancecap"] = N(1),
        ["insulationmultiplier"] = N(1),
        ["strokes"] = B(true),
        ["braindamagefx"] = B(true),
        ["forcesleep"] = B(true),
        ["lowmoodevents"] = B(true),
        ["liquidpushing"] = B(true),
        ["disfigurement"] = B(true),
        ["nosleeprestrictions"] = B(false),
        ["crushdepth"] = B(true),
        ["infinitelaststand"] = B(false),
        ["traderchance"] = N(33),
        ["traderitemamount"] = N(1),
        ["traderrepoffset"] = N(0),
        ["itemdecayrate"] = N(1),
        ["lockpickprecision"] = N(1),
        ["layermodifierchance"] = N(40),
        ["timebetweenearthquakes"] = N(1),
        ["temperatureoffset"] = N(0),
        ["allowlayerendmovement"] = B(false),
        ["oreamount"] = N(1),
        ["permanentaddiction"] = B(false),
        ["mindwiped"] = B(false),
        ["zeroskills"] = B(false),
        ["blind"] = B(false),
        ["amputee"] = B(false),
        ["immortalsnail"] = B(false)
    };

    public static IReadOnlyDictionary<string, SaveSettingValue> NormalValues => Baseline;

    public static SaveDifficultyPreset Normal { get; } = new("normal", "艰难", null);

    public static SaveDifficultyPreset Relaxed { get; } = new("relaxed", "轻松", new Dictionary<string, SaveSettingValue>
    {
        ["baselootdensity"] = N(1.15),
        ["basetrapdensity"] = N(0.8),
        ["ambientlight"] = N(1),
        ["timelimit"] = N(120),
        ["startingsupplies"] = N(2),
        ["enemystrength"] = N(0.75),
        ["xpgain"] = N(1.15),
        ["metabolismrate"] = N(0.7),
        ["healingrate"] = N(1.2),
        ["fracturepain"] = N(0.67),
        ["bleedrate"] = N(0.75),
        ["infectionspeed"] = N(0.7),
        ["infectionchance"] = N(0.75),
        ["minigamehandshake"] = N(0.66),
        ["fibrillationrate"] = N(0.8),
        ["moodnormalizationrate"] = N(1.25),
        ["staminaregen"] = N(1.1),
        ["insulationmultiplier"] = N(1.15),
        ["forcesleep"] = B(false),
        ["crushdepth"] = B(false),
        ["traderrepoffset"] = N(15),
        ["layermodifierchance"] = N(20),
        ["itemdecayrate"] = N(0.75),
        ["lockpickprecision"] = N(1.5),
        ["timebetweenearthquakes"] = N(1.5)
    });

    public static SaveDifficultyPreset Paradise { get; } = new("paradise", "天堂", new Dictionary<string, SaveSettingValue>
    {
        ["baselootdensity"] = N(1.5),
        ["basetrapdensity"] = N(0.35),
        ["ambientlight"] = N(2),
        ["timelimit"] = N(180),
        ["startingsupplies"] = N(2),
        ["enemystrength"] = N(0.5),
        ["allowlayerendmovement"] = B(true),
        ["xpgain"] = N(1.25),
        ["metabolismrate"] = N(0.7),
        ["healingrate"] = N(1.5),
        ["fracturepain"] = N(0.4),
        ["bleedrate"] = N(0.7),
        ["infectionspeed"] = N(0.5),
        ["infectionchance"] = N(0.75),
        ["fibrillationrate"] = N(0.6),
        ["moodnormalizationrate"] = N(1.25),
        ["staminaregen"] = N(1.2),
        ["minigamehandshake"] = N(0.2),
        ["insulationmultiplier"] = N(1.3),
        ["forcesleep"] = B(false),
        ["liquidpushing"] = B(false),
        ["crushdepth"] = B(false),
        ["infinitelaststand"] = B(true),
        ["attackdamage"] = N(1.5),
        ["encumbrancecap"] = N(1.5),
        ["traderrepoffset"] = N(25),
        ["traderchance"] = N(50),
        ["traderitemamount"] = N(1.25),
        ["itemdecayrate"] = N(0.5),
        ["layermodifierchance"] = N(10),
        ["lockpickprecision"] = N(2.5),
        ["bonuslimbarmor"] = N(0.25),
        ["timebetweenearthquakes"] = N(2)
    });

    public static SaveDifficultyPreset Desolate { get; } = new("desolate", "荒凉", new Dictionary<string, SaveSettingValue>
    {
        ["baselootdensity"] = N(0.4),
        ["basetrapdensity"] = N(1.5),
        ["startingsupplies"] = N(0),
        ["fracturepain"] = N(1.25),
        ["moodnormalizationrate"] = N(0.5),
        ["traderrepoffset"] = N(-10),
        ["traderchance"] = N(10),
        ["minigamehandshake"] = N(2),
        ["traderitemamount"] = N(3),
        ["layermodifierchance"] = N(100),
        ["temperatureoffset"] = N(-15),
        ["oreamount"] = N(0.5),
        ["timebetweenearthquakes"] = N(0.8)
    });

    public static SaveDifficultyPreset Unchipped { get; } = new("unchipped", "未植入芯片", new Dictionary<string, SaveSettingValue>
    {
        ["unchipped"] = B(true),
        ["timelimit"] = N(300),
        ["startingsupplies"] = N(0)
    });

    public static SaveDifficultyPreset Endless { get; } = new("endless", "无尽", new Dictionary<string, SaveSettingValue>
    {
        ["endless"] = B(true),
        ["lootmultiplier"] = N(0.95),
        ["trapincrease"] = N(0.06)
    });

    public static IReadOnlyList<SaveDifficultyPreset> All { get; } =
    [
        Normal,
        Relaxed,
        Paradise,
        Desolate,
        Unchipped,
        Endless
    ];

    /// <summary>Number of settings the current game version writes.</summary>
    public static int KnownKeyCount => Baseline.Count;

    /// <summary>
    /// Returns the preset the save exactly reproduces, or null when the save has
    /// an unknown key, is missing a key, stores a value with a different type, or
    /// differs in any value.
    /// </summary>
    public static SaveDifficultyPreset? Match(IReadOnlyDictionary<string, SaveSettingValue> values)
    {
        // Older saves are missing keys that today's game writes (for example
        // allowlayerendmovement); newer saves may carry keys this build has
        // never heard of. Both are "自定义" by design: no guessing.
        if (values.Count != Baseline.Count) return null;

        foreach (var key in values.Keys)
        {
            if (!Baseline.ContainsKey(key)) return null;
        }

        foreach (var preset in All)
        {
            var matched = true;

            foreach (var (key, expected) in preset.EffectiveSettings)
            {
                if (!values.TryGetValue(key, out var value) || !value.Matches(expected, Tolerance))
                {
                    matched = false;
                    break;
                }
            }

            if (matched) return preset;
        }

        return null;
    }

    /// <summary>Localised label for a key; unknown/custom keys map to "自定义".</summary>
    public static string LabelFor(string? key) =>
        All.FirstOrDefault(preset => string.Equals(preset.Key, key, StringComparison.Ordinal))?.Label
        ?? CustomLabel;

    internal static IReadOnlyDictionary<string, SaveSettingValue> Merge(
        IReadOnlyDictionary<string, SaveSettingValue> baseline,
        IReadOnlyDictionary<string, SaveSettingValue> overrides)
    {
        var merged = new Dictionary<string, SaveSettingValue>(baseline, StringComparer.Ordinal);
        foreach (var (key, value) in overrides) merged[key] = value;
        return merged;
    }
}
