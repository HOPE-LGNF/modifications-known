using System.Text.Json;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

/// <summary>
/// Installations the user removed from MK while leaving the game folder on
/// disk. Detection would otherwise resurrect them on the next refresh.
/// </summary>
public sealed class HiddenInstanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private HashSet<string>? _cache;

    public HiddenInstanceStore()
    {
        _path = Path.Combine(AppPaths.RootDirectory, "hidden-instances.json");
    }

    public bool IsHidden(string gameDirectory)
    {
        lock (_gate)
        {
            return Load().Contains(Key(gameDirectory));
        }
    }

    public void Hide(string gameDirectory)
    {
        lock (_gate)
        {
            var cache = Load();
            if (cache.Add(Key(gameDirectory))) Save(cache);
        }
    }

    public void Show(string gameDirectory)
    {
        lock (_gate)
        {
            var cache = Load();
            if (cache.Remove(Key(gameDirectory))) Save(cache);
        }
    }

    private HashSet<string> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(_path))
            {
                var items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path), JsonOptions);
                if (items is not null)
                    _cache = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // A broken hide list must not break detection.
        }

        return _cache ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save(HashSet<string> paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(paths.OrderBy(path => path), JsonOptions));
        }
        catch
        {
            // Keep the in-memory hide even if the profile is read-only.
        }
    }

    private static string Key(string gameDirectory) =>
        Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
}
