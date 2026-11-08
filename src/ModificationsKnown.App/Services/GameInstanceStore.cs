using System.Text.Json;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

/// <summary>
/// User-facing names for detected game installations. Paths are the identity;
/// the profile id alone cannot distinguish two copies of the same edition.
/// </summary>
public sealed class GameInstanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string>? _aliases;

    public GameInstanceStore()
    {
        _path = Path.Combine(AppPaths.RootDirectory, "game-instances.json");
    }

    public void EnsureAliases(IEnumerable<GameInstallation> installations)
    {
        lock (_gate)
        {
            var aliases = Load();
            var changed = false;
            foreach (var installation in installations)
            {
                var key = Key(installation.GameDirectory);
                if (aliases.ContainsKey(key)) continue;

                var baseAlias = installation.Profile.EditionLabel;
                var alias = baseAlias;
                var suffix = 2;
                while (aliases.Values.Contains(alias, StringComparer.OrdinalIgnoreCase))
                    alias = $"{baseAlias} ({suffix++})";

                aliases[key] = alias;
                changed = true;
            }

            if (changed) Save(aliases);
        }
    }

    public string? GetAlias(string gameDirectory)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(Key(gameDirectory));
        }
    }

    public void SetAlias(string gameDirectory, string alias)
    {
        lock (_gate)
        {
            var aliases = Load();
            aliases[Key(gameDirectory)] = string.IsNullOrWhiteSpace(alias)
                ? Path.GetFileName(gameDirectory.TrimEnd(Path.DirectorySeparatorChar))
                : alias.Trim();
            Save(aliases);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_aliases is not null) return _aliases;
        try
        {
            if (File.Exists(_path))
            {
                _aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(_path), JsonOptions);
            }
        }
        catch
        {
            // A broken metadata file must not break game detection.
        }

        return _aliases ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save(Dictionary<string, string> aliases)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(aliases, JsonOptions));
        }
        catch
        {
            // Keep the in-memory alias even if the profile is read-only.
        }
    }

    private static string Key(string gameDirectory) =>
        Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
}
