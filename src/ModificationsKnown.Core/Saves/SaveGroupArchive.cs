namespace ModificationsKnown.Core.Saves;

/// <summary>
/// Versioned manifest stored as <c>manifest.json</c> at the root of a save-group
/// archive. It describes the group and every archived file by relative path,
/// size and SHA-256. It never records absolute paths, the user name, the game
/// installation directory or the on-disk profile root, so an archive can be
/// moved between machines and imported without leaking local layout.
/// </summary>
public sealed class SaveGroupArchiveManifest
{
    public int SchemaVersion { get; set; } = SaveGroupArchiveFormat.SchemaVersion;

    public Guid GroupId { get; set; }

    public string GroupName { get; set; } = string.Empty;

    /// <summary>Game edition the archive came from, e.g. Full or Demo.</summary>
    public string SourceEdition { get; set; } = SaveGroupArchiveFormat.UnknownSource;

    /// <summary>Game version the archive came from, or "unknown".</summary>
    public string SourceVersion { get; set; } = SaveGroupArchiveFormat.UnknownSource;

    public DateTimeOffset ExportedAt { get; set; }

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public List<SaveGroupArchiveFile> Files { get; set; } = [];
}

/// <summary>One archived file. <see cref="Path"/> is relative to the group root
/// and always uses '/' separators. <see cref="Sha256"/> is lowercase hex.</summary>
public sealed record SaveGroupArchiveFile(string Path, long Size, string Sha256);

/// <summary>Layout rules shared by export, inspection and import.</summary>
public static class SaveGroupArchiveFormat
{
    public const int SchemaVersion = 1;

    public const string ManifestFileName = SaveGroupFormat.ArchiveManifestFileName;

    public const string FilesPrefix = SaveGroupFormat.ArchiveFilesPrefix;

    public const string UnknownSource = "unknown";

    /// <summary>True for a normalized, relative, forward-slash path that stays
    /// inside the archive's files/ directory.</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains('\\')) return false;
        if (path.Contains(':')) return false;
        if (path.Length > 0 && path[0] == '/') return false;
        if (Path.IsPathRooted(path)) return false;
        if (path.Any(char.IsControl)) return false;

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) return false;
            if (segment is "." or "..") return false;
        }

        return true;
    }

    /// <summary>Relative path with '/' separators, as stored in the manifest.</summary>
    public static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');
}
