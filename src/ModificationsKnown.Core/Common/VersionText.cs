namespace ModificationsKnown.Core.Common;

public static class VersionText
{
    /// <summary>True when <paramref name="candidate"/> is a newer numeric
    /// version than <paramref name="current"/>. Falls back to “unknown current
    /// means update” so first-time version detection is not blocked.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current)) return true;

        var left = Numbers(candidate);
        var right = Numbers(current);
        if (left.Count == 0 || right.Count == 0)
            return !string.Equals(candidate.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);

        for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
        {
            var a = index < left.Count ? left[index] : 0;
            var b = index < right.Count ? right[index] : 0;
            if (a > b) return true;
            if (a < b) return false;
        }

        return false;
    }

    private static List<int> Numbers(string value)
    {
        var result = new List<int>();
        var current = 0;
        var hasDigits = false;

        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                current = current * 10 + (character - '0');
                hasDigits = true;
            }
            else if (hasDigits)
            {
                result.Add(current);
                current = 0;
                hasDigits = false;
            }
        }

        if (hasDigits) result.Add(current);
        return result;
    }

    public static string Label(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "版本未知";

        var trimmed = version.Trim();
        return trimmed.StartsWith('v') || trimmed.StartsWith('V')
            ? $"v{trimmed[1..]}"
            : $"v{trimmed}";
    }
}
