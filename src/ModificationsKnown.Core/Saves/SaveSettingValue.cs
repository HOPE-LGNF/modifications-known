namespace ModificationsKnown.Core.Saves;

public enum SaveSettingKind
{
    Number,
    Boolean,
    Text,

    /// <summary>A value whose JSON type the reader cannot interpret (object,
    /// array, null). Kept so the key still counts as present and unknown: a save
    /// carrying one of these must never match an official preset.</summary>
    Unsupported
}

/// <summary>
/// One <c>runSettings</c> value with its JSON type preserved. The game stores
/// numbers, booleans and strings in the same list; flattening everything to a
/// double (as an earlier build did) silently treated booleans and strings as
/// "not comparable" and could name a preset from a handful of matching keys.
/// </summary>
public readonly record struct SaveSettingValue(SaveSettingKind Kind, double Number, bool Boolean, string Text)
{
    public static SaveSettingValue FromNumber(double value) =>
        new(SaveSettingKind.Number, value, false, string.Empty);

    public static SaveSettingValue FromBoolean(bool value) =>
        new(SaveSettingKind.Boolean, 0, value, string.Empty);

    public static SaveSettingValue FromText(string value) =>
        new(SaveSettingKind.Text, 0, false, value ?? string.Empty);

    /// <summary>Stands in for a value whose JSON type cannot be interpreted.</summary>
    public static SaveSettingValue Unsupported { get; } =
        new(SaveSettingKind.Unsupported, 0, false, string.Empty);

    /// <summary>Numbers use a tolerance; booleans and strings compare exactly.
    /// An unsupported value never matches anything.</summary>
    public bool Matches(SaveSettingValue other, double tolerance)
    {
        if (Kind != other.Kind) return false;

        return Kind switch
        {
            SaveSettingKind.Number => Math.Abs(Number - other.Number) <= tolerance,
            SaveSettingKind.Boolean => Boolean == other.Boolean,
            SaveSettingKind.Text => string.Equals(Text, other.Text, StringComparison.Ordinal),
            _ => false
        };
    }
}
