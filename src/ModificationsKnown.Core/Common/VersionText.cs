namespace ModificationsKnown.Core.Common;

public static class VersionText
{
    public static string Label(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "版本未知";
        var trimmed = version.Trim();
        return trimmed.StartsWith('v') || trimmed.StartsWith('V') ? $"v{trimmed[1..]}" : $"v{trimmed}";
    }
}
