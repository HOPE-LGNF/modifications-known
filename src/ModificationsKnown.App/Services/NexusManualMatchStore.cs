using System.Text.Json;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

/// <summary>
/// Persists user-confirmed Nexus associations. Auto-matching is only a
/// suggestion; a manual association wins and is retained across scans.
/// </summary>
public sealed class NexusManualMatchStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, NexusMatch>? _cache;

    public NexusManualMatchStore()
    {
        _path = Path.Combine(AppPaths.CacheDirectory, "nexus-manual-matches.json");
    }

    public NexusMatch? Get(string gameDirectory, string packageId)
    {
        lock (_gate)
        {
            var cache = Load();
            return cache.GetValueOrDefault(Key(gameDirectory, packageId));
        }
    }

    public void Set(string gameDirectory, string packageId, NexusMatch match)
    {
        lock (_gate)
        {
            var cache = Load();
            cache[Key(gameDirectory, packageId)] = match;
            Save(cache);
        }
    }

    public void Remove(string gameDirectory, string packageId)
    {
        lock (_gate)
        {
            var cache = Load();
            if (cache.Remove(Key(gameDirectory, packageId)))
                Save(cache);
        }
    }

    private Dictionary<string, NexusMatch> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _cache = JsonSerializer.Deserialize<Dictionary<string, NexusMatch>>(json, JsonOptions)
                         ?? new Dictionary<string, NexusMatch>();
            }
        }
        catch
        {
            // A corrupt cache must not break scanning; manual links can be set again.
        }

        return _cache ??= new Dictionary<string, NexusMatch>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save(Dictionary<string, NexusMatch> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch
        {
            // Keep the in-memory association even if disk is read-only.
        }
    }

    private static string Key(string gameDirectory, string packageId) =>
        $"{Path.GetFullPath(gameDirectory)}|{packageId}".ToLowerInvariant();
}
