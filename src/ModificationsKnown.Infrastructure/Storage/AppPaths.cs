using System.Text.Json;

namespace ModificationsKnown.Infrastructure.Storage;

public static class AppPaths
{
    private const string ApplicationFolderName = "ModificationsKnown";

    /// <summary>Default per-user AppData root. The pointer file that redirects
    /// the data root always lives here, so the override survives restarts.</summary>
    public static string DefaultRootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ApplicationFolderName);

    public static string RootDirectory { get; } = ResolveRoot();

    public static string SettingsFile { get; } = Path.Combine(RootDirectory, "settings.json");

    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");

    /// <summary>Save profiles deliberately stay under the default AppData root:
    /// the live game folder is a junction into them, so relocating the rest of
    /// the data root must not move or orphan them.</summary>
    public static string SaveProfilesDirectory { get; } = Path.Combine(DefaultRootDirectory, "save-profiles");

    public static string CacheDirectory { get; } = Path.Combine(RootDirectory, "cache");

    public static string DownloadsDirectory { get; } = Path.Combine(CacheDirectory, "downloads");

    public static string TempDirectory { get; } = Path.Combine(CacheDirectory, "temp");

    public static string LocationFile => Path.Combine(DefaultRootDirectory, "location.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    private static string ResolveRoot()
    {
        try
        {
            if (File.Exists(LocationFile))
            {
                var location = JsonSerializer.Deserialize<LocationFileModel>(File.ReadAllText(LocationFile));
                if (!string.IsNullOrWhiteSpace(location?.DataRoot))
                    return Path.GetFullPath(location.DataRoot);
            }
        }
        catch
        {
            // A broken pointer falls back to the default root.
        }

        return DefaultRootDirectory;
    }

    private sealed class LocationFileModel
    {
        public string? DataRoot { get; set; }
    }
}
