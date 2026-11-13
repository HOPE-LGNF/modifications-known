using System.Text.Json;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

/// <summary>Persisted instance record. Detection only refreshes this list on an
/// explicit request; normal startup and startup-after-delete never re-scan
/// Steam, so a removed instance cannot silently come back.</summary>
public sealed record SavedGameInstance(
    string Path,
    string Alias,
    string ProfileId,
    string Edition,
    string SteamAppId,
    string? ExecutablePath);

/// <summary>
/// User-facing names and membership for detected game installations. Paths are
/// the identity; the profile id alone cannot distinguish two copies of the same
/// edition. Schema v2 stores full instance records instead of a path→alias map.
/// </summary>
public sealed class GameInstanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<SavedGameInstance>? _cache;

    public GameInstanceStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.RootDirectory, "game-instances.json");
    }

    private sealed class Envelope
    {
        public int Version { get; set; } = 2;

        public List<SavedGameInstance> Instances { get; set; } = [];
    }

    /// <summary>True once the store file exists. An empty list then means the
    /// user removed every instance, not “first run”, so startup must not scan.</summary>
    public bool Exists => File.Exists(_path);

    public IReadOnlyList<SavedGameInstance> LoadInstances()
    {
        lock (_gate)
        {
            return Load().OrderBy(instance => instance.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public SavedGameInstance? Get(string gameDirectory)
    {
        lock (_gate)
        {
            return Load().FirstOrDefault(instance => PathEquals(instance.Path, gameDirectory));
        }
    }

    public string? GetAlias(string gameDirectory) => Get(gameDirectory)?.Alias;

    public void Upsert(GameInstallation installation, string? alias = null)
    {
        lock (_gate)
        {
            var list = Load();
            var index = list.FindIndex(item => PathEquals(item.Path, installation.GameDirectory));
            var existing = index >= 0 ? list[index] : null;

            var name = (alias ?? existing?.Alias ?? installation.Alias).Trim();
            if (string.IsNullOrWhiteSpace(name)) name = installation.Profile.EditionLabel;

            // Keep aliases unique so the top ComboBox stays unambiguous.
            var suffix = 2;
            var candidate = name;
            while (list.Any(item => !PathEquals(item.Path, installation.GameDirectory)
                                    && string.Equals(item.Alias, candidate, StringComparison.OrdinalIgnoreCase)))
                candidate = $"{name} ({suffix++})";
            name = candidate;

            var saved = new SavedGameInstance(
                Path.GetFullPath(installation.GameDirectory),
                name,
                installation.Profile.Id,
                installation.Profile.Edition.ToString(),
                installation.Profile.SteamAppId,
                installation.ExecutablePath);

            if (index >= 0) list[index] = saved;
            else list.Add(saved);

            Save(list);
        }
    }

    public void SetAlias(string gameDirectory, string alias)
    {
        lock (_gate)
        {
            var list = Load();
            var index = list.FindIndex(item => PathEquals(item.Path, gameDirectory));
            if (index < 0) return;

            var trimmed = alias.Trim();
            if (trimmed.Length == 0) return;

            list[index] = list[index] with { Alias = trimmed };
            Save(list);
        }
    }

    public bool Remove(string gameDirectory)
    {
        lock (_gate)
        {
            var list = Load();
            var removed = list.RemoveAll(item => PathEquals(item.Path, gameDirectory)) > 0;
            if (removed) Save(list);
            return removed;
        }
    }

    public bool RemoveAlias(string gameDirectory) => Remove(gameDirectory);

    /// <summary>Kept for the first-run migration path: store every detected
    /// installation, preserving aliases that already exist.</summary>
    public void EnsureAliases(IEnumerable<GameInstallation> installations)
    {
        foreach (var installation in installations)
        {
            var existing = GetAlias(installation.GameDirectory);
            Upsert(installation, existing);
        }
    }

    private List<SavedGameInstance> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var envelope = TryDeserialize<Envelope>(json);
                if (envelope?.Instances is { Count: > 0 })
                {
                    _cache = envelope.Instances
                        .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                        .ToList();
                    return _cache;
                }

                // v1: { "<path>": "<alias>" }
                var legacy = TryDeserialize<Dictionary<string, string>>(json);
                if (legacy is { Count: > 0 })
                {
                    _cache = legacy.Select(pair =>
                        {
                            var profile = GuessProfile(pair.Key);
                            return new SavedGameInstance(
                                pair.Key,
                                pair.Value,
                                profile.Id,
                                profile.Edition.ToString(),
                                profile.SteamAppId,
                                null);
                        })
                        .ToList();
                    Save(_cache);
                    return _cache;
                }
            }
        }
        catch
        {
            // A broken file must not break game detection.
        }

        return _cache ??= [];
    }

    private static T? TryDeserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static GameProfile GuessProfile(string path) =>
        path.Contains("demo", StringComparison.OrdinalIgnoreCase)
            ? KnownGameProfiles.Demo
            : KnownGameProfiles.Full;

    private void Save(List<SavedGameInstance> instances)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var envelope = new Envelope { Instances = instances };
            File.WriteAllText(_path, JsonSerializer.Serialize(envelope, JsonOptions));
            _cache = instances;
        }
        catch
        {
            // Keep the in-memory list even if the profile is read-only.
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
