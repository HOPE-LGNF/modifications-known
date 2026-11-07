using System.Text.Json;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Mods;

namespace ModificationsKnown.Infrastructure.Storage;

/// <summary>
/// Sidecar provenance store used until every package carries an in-folder
/// manifest. It is keyed by game directory + package folder name so the demo
/// and full editions do not contaminate each other.
/// </summary>
public sealed class JsonInstalledSourceStore : IInstalledSourceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IAppLogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private Dictionary<string, InstalledSource>? _cache;

    public JsonInstalledSourceStore(IAppLogger? logger = null, string? path = null)
    {
        _logger = logger;
        _path = path ?? Path.Combine(AppPaths.RootDirectory, "installed-sources.json");
    }

    public InstalledSource? Get(string gameDirectory, string packageName)
    {
        var cache = Load();
        return cache.GetValueOrDefault(Key(gameDirectory, packageName));
    }

    public async Task RecordAsync(
        IReadOnlyList<InstalledSource> sources,
        CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = Load();
            foreach (var source in sources)
                cache[Key(source.GameDirectory, source.PackageName)] = source;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            var json = JsonSerializer.Serialize(cache.Values.ToList(), JsonOptions);
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
            _cache = cache;
            _logger?.Debug($"Recorded Nexus source(s) for {sources.Count} package(s)");
        }
        catch (Exception exception)
        {
            _logger?.Warn($"保存安装来源失败：{exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, InstalledSource> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var items = JsonSerializer.Deserialize<List<InstalledSource>>(json, JsonOptions) ?? [];
                _cache = items
                    .GroupBy(item => Key(item.GameDirectory, item.PackageName), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
                return _cache;
            }
        }
        catch (Exception exception)
        {
            _logger?.Warn($"读取安装来源失败：{exception.Message}");
        }

        _cache = new Dictionary<string, InstalledSource>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private static string Key(string gameDirectory, string packageName) =>
        $"{Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant()}|{packageName.ToLowerInvariant()}";
}
