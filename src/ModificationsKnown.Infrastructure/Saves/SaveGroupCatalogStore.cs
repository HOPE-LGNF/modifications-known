using System.Text.Json;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// Reads and writes <c>save-groups.json</c>. Writes are atomic (temp file in
/// the same directory, then replace). A corrupt index is moved aside instead of
/// being silently overwritten; group directories can always rebuild it.
/// </summary>
public sealed class SaveGroupCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SaveGroupCatalogStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.RootDirectory, SaveGroupFormat.CatalogFileName);
    }

    public string CatalogPath => _path;

    public SaveGroupCatalog Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new SaveGroupCatalog();

            try
            {
                var catalog = JsonSerializer.Deserialize<SaveGroupCatalog>(File.ReadAllText(_path), JsonOptions);
                return catalog ?? new SaveGroupCatalog();
            }
            catch
            {
                TryPreserveCorruptFile();
                return new SaveGroupCatalog();
            }
        }
    }

    public void Save(SaveGroupCatalog catalog)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var temporary = $"{_path}.tmp-{Guid.NewGuid():N}";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(catalog, JsonOptions));
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public static SaveGroupInstallationEntry GetOrCreate(
        SaveGroupCatalog catalog,
        string installationKey,
        string gameDirectory)
    {
        var entry = catalog.Installations.FirstOrDefault(candidate =>
            string.Equals(candidate.InstallationKey, installationKey, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) return entry;

        entry = new SaveGroupInstallationEntry
        {
            InstallationKey = installationKey,
            GameDirectory = Path.GetFullPath(gameDirectory)
        };
        catalog.Installations.Add(entry);
        return entry;
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            var preserved = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_path, preserved, overwrite: false);
        }
        catch
        {
            // Leaving the original file in place is still better than deleting it.
        }
    }
}
