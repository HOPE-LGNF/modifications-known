using System.Text.Json;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

/// <summary>
/// Optional relocation of the MK data root (settings, logs, caches). The
/// pointer lives in the default AppData folder and is read at process start, so
/// a change always requires a restart.
/// </summary>
public sealed class DataLocationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string CurrentRoot => AppPaths.RootDirectory;

    public string DefaultRoot => AppPaths.DefaultRootDirectory;

    public bool IsCustom => File.Exists(AppPaths.LocationFile) || !PathEquals(CurrentRoot, DefaultRoot);

    public string SetRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("数据目录不能为空。", nameof(path));

        var full = Path.GetFullPath(path);
        if (PathEquals(full, DefaultRoot))
        {
            Reset();
            return DefaultRoot;
        }

        Directory.CreateDirectory(full);

        // Carry metadata, settings and custom themes over. Save profiles stay in
        // the default root (the live game junction points there); caches and
        // logs are recreated on the next start and old logs remain in place.
        foreach (var name in new[]
                 {
                     "settings.json",
                     "game-instances.json",
                     "mod-groups.json",
                     "installed-sources.json"
                 })
        {
            CopyIfMissing(Path.Combine(AppPaths.RootDirectory, name), Path.Combine(full, name));
        }

        CopyIfMissing(
            Path.Combine(AppPaths.CacheDirectory, "nexus-manual-matches.json"),
            Path.Combine(full, "cache", "nexus-manual-matches.json"));
        CopyThemes(Path.Combine(AppPaths.RootDirectory, "themes"), Path.Combine(full, "themes"));

        Directory.CreateDirectory(AppPaths.DefaultRootDirectory);
        File.WriteAllText(
            AppPaths.LocationFile,
            JsonSerializer.Serialize(new { dataRoot = full }, JsonOptions));

        return full;
    }

    public void Reset()
    {
        if (File.Exists(AppPaths.LocationFile)) File.Delete(AppPaths.LocationFile);
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
    }

    private static void CopyThemes(string source, string destination)
    {
        if (!Directory.Exists(source)) return;

        foreach (var file in Directory.EnumerateFiles(source, "*.json"))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(destination);
            File.Copy(file, target);
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
