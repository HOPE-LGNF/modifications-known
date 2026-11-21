using System.Globalization;

namespace ModificationsKnown.Core.Updates;

/// <summary>
/// Launcher-only semantic version (SemVer 2.0): a single optional v/V prefix,
/// three numeric parts, an optional prerelease and optional build metadata.
/// Build metadata is kept for display and ignored for ordering. Malformed,
/// overflowing or absurdly long input is rejected rather than guessed at.
/// </summary>
public readonly struct LauncherVersion : IComparable<LauncherVersion>, IEquatable<LauncherVersion>
{
    private const int MaxLength = 128;

    private readonly int _major;
    private readonly int _minor;
    private readonly int _patch;
    private readonly string _prerelease;
    private readonly string _metadata;
    private readonly bool _parsed;

    private LauncherVersion(int major, int minor, int patch, string prerelease, string metadata)
    {
        _major = major;
        _minor = minor;
        _patch = patch;
        _prerelease = prerelease;
        _metadata = metadata;
        _parsed = true;
    }

    public bool HasPrerelease => _parsed && _prerelease.Length > 0;

    public static bool TryParse(string? text, out LauncherVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.Length > MaxLength) return false;
        if (value[0] is 'v' or 'V') value = value[1..];
        if (value.Length == 0) return false;

        var metadata = string.Empty;
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            metadata = value[(plus + 1)..];
            value = value[..plus];
            if (!ValidIdentifiers(metadata, allowLeadingZeros: true)) return false;
        }

        var prerelease = string.Empty;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (!ValidIdentifiers(prerelease, allowLeadingZeros: false)) return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 3) return false;
        if (!TryNumber(parts[0], out var major)
            || !TryNumber(parts[1], out var minor)
            || !TryNumber(parts[2], out var patch))
            return false;

        version = new LauncherVersion(major, minor, patch, prerelease, metadata);
        return true;
    }

    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        if (text.Length == 0) return false;
        if (text.Length > 1 && text[0] == '0') return false;

        foreach (var character in text)
        {
            if (character is < '0' or > '9') return false;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool ValidIdentifiers(string text, bool allowLeadingZeros)
    {
        if (text.Length == 0) return false;

        foreach (var part in text.Split('.'))
        {
            if (part.Length == 0) return false;

            var allDigits = true;
            foreach (var character in part)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-') return false;
                if (!char.IsAsciiDigit(character)) allDigits = false;
            }

            if (!allowLeadingZeros && allDigits && part.Length > 1 && part[0] == '0') return false;
        }

        return true;
    }

    public int CompareTo(LauncherVersion other)
    {
        var comparison = _major.CompareTo(other._major);
        if (comparison != 0) return comparison;

        comparison = _minor.CompareTo(other._minor);
        if (comparison != 0) return comparison;

        comparison = _patch.CompareTo(other._patch);
        if (comparison != 0) return comparison;

        return ComparePrerelease(_prerelease, other._prerelease);
    }

    private static int ComparePrerelease(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0) return 0;

        // A release outranks any prerelease of the same core version.
        if (left.Length == 0) return 1;
        if (right.Length == 0) return -1;

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var shared = Math.Min(leftParts.Length, rightParts.Length);

        for (var index = 0; index < shared; index++)
        {
            var comparison = CompareIdentifier(leftParts[index], rightParts[index]);
            if (comparison != 0) return comparison;
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);

        if (leftNumeric && rightNumeric)
        {
            // No leading zeros are allowed, so a longer digit run is a bigger number.
            var byLength = left.Length.CompareTo(right.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
        }

        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character)) return false;
        }

        return value.Length > 0;
    }

    public bool Equals(LauncherVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is LauncherVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_major, _minor, _patch, _prerelease);

    public static bool operator ==(LauncherVersion left, LauncherVersion right) => left.Equals(right);

    public static bool operator !=(LauncherVersion left, LauncherVersion right) => !left.Equals(right);

    public static bool operator <(LauncherVersion left, LauncherVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(LauncherVersion left, LauncherVersion right) => left.CompareTo(right) > 0;

    public override string ToString()
    {
        var text = $"{_major}.{_minor}.{_patch}";
        if (_prerelease.Length > 0) text += "-" + _prerelease;
        if (_metadata.Length > 0) text += "+" + _metadata;
        return text;
    }
}
