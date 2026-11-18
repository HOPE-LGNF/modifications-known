using System.Security.Cryptography;
using System.Text;

namespace ModificationsKnown.Core.Saves;

/// <summary>
/// On-disk identity rules for save groups. A save group isolates the full
/// LocalLow profile directory; the installation key is the same short path
/// hash the single-profile layout used, so existing profiles migrate in place.
/// </summary>
public static class SaveGroupFormat
{
    public const int ManifestSchemaVersion = 1;

    public const int CatalogSchemaVersion = 1;

    public const string ManifestFileName = ".mk-save-group.json";

    public const string LegacyProfileMarkerFileName = ".mk-save-profile";

    public const string CatalogFileName = "save-groups.json";

    public const string ArchiveManifestFileName = "manifest.json";

    public const string ArchiveFilesPrefix = "files/";

    public const int MaxNameLength = 48;

    public static string GetInstallationKey(string gameDirectory)
    {
        var normalized = Path.GetFullPath(gameDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string NormalizeName(string? name) => (name ?? string.Empty).Trim();

    /// <summary>Returns a user-facing error, or null when the name is usable.</summary>
    public static string? ValidateName(string? name)
    {
        var trimmed = NormalizeName(name);
        if (trimmed.Length == 0) return "存档组名称不能为空。";
        if (trimmed.Length > MaxNameLength) return $"存档组名称不能超过 {MaxNameLength} 个字符。";
        if (trimmed is "." or "..") return "存档组名称无效。";

        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
            .ToHashSet();
        if (trimmed.Any(character => char.IsControl(character) || invalid.Contains(character)))
            return "存档组名称不能包含路径或特殊字符。";

        return null;
    }

    public static bool IsManifestFile(string fileName) =>
        string.Equals(fileName, ManifestFileName, StringComparison.OrdinalIgnoreCase);

    public static bool IsLegacyMarker(string fileName) =>
        string.Equals(fileName, LegacyProfileMarkerFileName, StringComparison.OrdinalIgnoreCase);

    public static bool IsInternalFile(string fileName) =>
        IsManifestFile(fileName) || IsLegacyMarker(fileName);
}
