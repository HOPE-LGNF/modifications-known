namespace ModificationsKnown.Infrastructure.Storage;

public static class AppPaths
{
    private const string ApplicationFolderName = "ModificationsKnown";

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ApplicationFolderName);

    public static string SettingsFile { get; } = Path.Combine(RootDirectory, "settings.json");

    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");

    public static string CacheDirectory { get; } = Path.Combine(RootDirectory, "cache");

    public static string DownloadsDirectory { get; } = Path.Combine(CacheDirectory, "downloads");

    public static string TempDirectory { get; } = Path.Combine(CacheDirectory, "temp");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}
