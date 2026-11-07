namespace ModificationsKnown.Core.Common;

/// <summary>
/// Path helpers shared by installers and tests. All checks operate on the
/// canonical full path, never on user-supplied relative fragments.
/// </summary>
public static class PathSafety
{
    public static bool IsWithinDirectory(string candidatePath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directoryPath))
            return false;

        var root = Path.GetFullPath(directoryPath);
        var candidate = Path.GetFullPath(candidatePath);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        return candidate.StartsWith(root, comparison)
               || string.Equals(candidate, root.TrimEnd(Path.DirectorySeparatorChar), comparison);
    }

    /// <summary>
    /// Rejects rooted paths, parent traversal and invalid file names before a
    /// path is combined with a destination root.
    /// </summary>
    public static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;

        if (relativePath.Length >= 2 && char.IsLetter(relativePath[0]) && relativePath[1] == ':')
            return false;

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal)) return false;
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
            return false;

        return normalized.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }

    public static string GetSafeFileName(string? value, string fallback = "unknown")
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
            .ToHashSet();
        var chars = value.Trim()
            .Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c)
            .ToArray();
        var result = new string(chars).Trim().TrimEnd('.');

        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }
}
