namespace ModificationsKnown.Core.Saves;

public enum SaveSettingKind
{
    Number,
    Boolean,
    Text
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

    /// <summary>Numbers use a tolerance; booleans and strings compare exactly.</summary>
    public bool Matches(SaveSettingValue other, double tolerance)
    {
        if (Kind != other.Kind) return false;

        return Kind switch
        {
            SaveSettingKind.Number => Math.Abs(Number - other.Number) <= tolerance,
            SaveSettingKind.Boolean => Boolean == other.Boolean,
            _ => string.Equals(Text, other.Text, StringComparison.Ordinal)
        };
    }
}
