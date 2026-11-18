using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>Reads and writes the per-group manifest that travels with the
/// directory and lets a scan rebuild group identity after an Explorer restore.</summary>
public static class SaveGroupManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetManifestPath(string groupDirectory) =>
        Path.Combine(groupDirectory, SaveGroupFormat.ManifestFileName);

    public static bool TryRead(string groupDirectory, out SaveGroupManifest manifest)
    {
        manifest = new SaveGroupManifest();
        var path = GetManifestPath(groupDirectory);
        if (!File.Exists(path)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<SaveGroupManifest>(File.ReadAllText(path), JsonOptions);
            if (parsed is null) return false;
            if (parsed.SchemaVersion != SaveGroupFormat.ManifestSchemaVersion) return false;
            if (string.IsNullOrWhiteSpace(parsed.InstallationKey)) return false;
            if (SaveGroupFormat.ValidateName(parsed.Name) is not null) return false;
            manifest = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Write(string groupDirectory, SaveGroupManifest manifest)
    {
        Directory.CreateDirectory(groupDirectory);
        File.WriteAllText(GetManifestPath(groupDirectory), JsonSerializer.Serialize(manifest, JsonOptions));
    }
}

internal static class SaveFileHasher
{
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool HashesMatch(string leftPath, string rightPath)
    {
        var left = new FileInfo(leftPath);
        var right = new FileInfo(rightPath);
        if (!right.Exists || left.Length != right.Length) return false;
        return string.Equals(ComputeSha256(leftPath), ComputeSha256(rightPath), StringComparison.OrdinalIgnoreCase);
    }
}
