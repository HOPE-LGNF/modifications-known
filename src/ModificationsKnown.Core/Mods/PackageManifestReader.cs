using System.Text.Json;

namespace ModificationsKnown.Core.Mods;

public static class PackageManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string GetManifestPath(string packageDirectory) =>
        Path.Combine(packageDirectory, PackageManifest.DirectoryName, PackageManifest.FileName);

    public static bool TryRead(
        string packageDirectory,
        out PackageManifest? manifest,
        out string error)
    {
        manifest = null;
        error = string.Empty;

        var path = GetManifestPath(packageDirectory);
        if (!File.Exists(path))
        {
            error = "没有安装清单。";
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<PackageManifest>(json, JsonOptions);
            if (parsed is null)
            {
                error = "安装清单为空。";
                return false;
            }

            if (parsed.SchemaVersion != PackageManifest.CurrentSchemaVersion)
            {
                error = $"不支持的清单版本：{parsed.SchemaVersion}。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.PackageName)
                || parsed.Files is null
                || parsed.Files.Any(file => !IsSafeRelativePath(file.RelativePath)))
            {
                error = "安装清单包含无效的包名或路径。";
                return false;
            }

            manifest = parsed;
            return true;
        }
        catch (Exception exception)
        {
            error = $"安装清单无法读取：{exception.Message}";
            return false;
        }
    }

    public static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        if (relativePath.Length >= 2 && char.IsLetter(relativePath[0]) && relativePath[1] == ':') return false;

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal)) return false;
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == "..")) return false;
        return normalized.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }
}
