using System.Text.Json;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

public sealed record NexusMatch(string Name, string GameScopedId, string ModUid)
{
    public string PageUrl(string gameDomain) =>
        $"https://www.nexusmods.com/{gameDomain}/mods/{GameScopedId}";
}

/// <summary>
/// Matches installed packages to Nexus entries by exact (case-insensitive)
/// name. Results, including misses, are cached so opening the installed page
/// does not query Nexus for every package every time.
/// </summary>
public sealed class NexusMatchService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private readonly INexusModsClient _client;
    private readonly IAppSettingsService _settings;
    private readonly IAppLogger _logger;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, CacheEntry>? _cache;

    public NexusMatchService(INexusModsClient client, IAppSettingsService settings, IAppLogger logger)
    {
        _client = client;
        _settings = settings;
        _logger = logger;
        _cachePath = Path.Combine(AppPaths.CacheDirectory, "nexus-matches.json");
    }

    public async Task<NexusMatch?> ResolveAsync(
        string packageId,
        string name,
        string? version,
        CancellationToken cancellationToken = default)
    {
        var key = $"{packageId}|{name}|{version}";
        var cache = await LoadCacheAsync();
        if (cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.CheckedAt < CacheLifetime)
            return cached.Match;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check after waiting; another package may have filled it.
            cache = await LoadCacheAsync();
            if (cache.TryGetValue(key, out cached) && DateTimeOffset.UtcNow - cached.CheckedAt < CacheLifetime)
                return cached.Match;

            NexusMatch? match = null;
            try
            {
                var result = await _client.SearchModsAsync(
                    _settings.Current.NexusGameDomain,
                    new NexusSearchQuery(name, PageSize: 10),
                    cancellationToken);

                var exact = result.Items.FirstOrDefault(item =>
                    string.Equals(Normalize(item.Name), Normalize(name), StringComparison.OrdinalIgnoreCase));

                if (exact is not null)
                {
                    match = new NexusMatch(exact.Name, exact.GameScopedId, exact.Id);
                    _logger.Debug($"Nexus match: {name} -> {exact.Name} (#{exact.GameScopedId})");
                }
                else
                {
                    _logger.Debug($"Nexus match: {name} -> local only");
                }
            }
            catch (Exception exception)
            {
                _logger.Debug($"Nexus match failed for {name}: {exception.Message}");
            }

            cache[key] = new CacheEntry(match, DateTimeOffset.UtcNow);
            await SaveCacheAsync(cache);
            return match;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, CacheEntry>> LoadCacheAsync()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(_cachePath))
            {
                var json = await File.ReadAllTextAsync(_cachePath);
                _cache = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json)
                         ?? new Dictionary<string, CacheEntry>();
                return _cache;
            }
        }
        catch
        {
            // A corrupt cache is not worth failing the installed page.
        }

        _cache = new Dictionary<string, CacheEntry>();
        return _cache;
    }

    private async Task SaveCacheAsync(Dictionary<string, CacheEntry> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_cachePath, json);
        }
        catch (Exception exception)
        {
            _logger.Debug($"Nexus match cache write failed: {exception.Message}");
        }
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private sealed class CacheEntry
    {
        public CacheEntry()
        {
        }

        public CacheEntry(NexusMatch? match, DateTimeOffset checkedAt)
        {
            Match = match;
            CheckedAt = checkedAt;
        }

        public NexusMatch? Match { get; set; }

        public DateTimeOffset CheckedAt { get; set; }
    }
}
