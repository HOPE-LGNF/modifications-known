using System.Text.Json;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

public sealed class ModGroupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, List<ModGroup>>? _cache;

    public ModGroupStore()
    {
        _path = Path.Combine(AppPaths.RootDirectory, "mod-groups.json");
    }

    public IReadOnlyList<ModGroup> Get(string gameDirectory)
    {
        lock (_gate)
        {
            var cache = Load();
            return cache.TryGetValue(Key(gameDirectory), out var groups)
                ? groups.OrderBy(group => group.CreatedAt).ToList()
                : [];
        }
    }

    public void Add(string gameDirectory, ModGroup group)
    {
        lock (_gate)
        {
            var cache = Load();
            var key = Key(gameDirectory);
            if (!cache.TryGetValue(key, out var groups))
            {
                groups = [];
                cache[key] = groups;
            }

            groups.Add(group);
            Save(cache);
        }
    }

    public void Remove(string gameDirectory, string id)
    {
        lock (_gate)
        {
            var cache = Load();
            if (!cache.TryGetValue(Key(gameDirectory), out var groups)) return;
            groups.RemoveAll(group => group.Id == id);
            Save(cache);
        }
    }

    private Dictionary<string, List<ModGroup>> Load()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (File.Exists(_path))
            {
                _cache = JsonSerializer.Deserialize<Dictionary<string, List<ModGroup>>>(
                    File.ReadAllText(_path), JsonOptions);
            }
        }
        catch
        {
            // A broken group file must not block the installed page.
        }

        return _cache ??= new Dictionary<string, List<ModGroup>>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save(Dictionary<string, List<ModGroup>> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch
        {
            // Keep the in-memory group even if disk is read-only.
        }
    }

    private static string Key(string gameDirectory) =>
        Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
}
