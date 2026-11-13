using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.Infrastructure.Games;

/// <summary>
/// Locates CU installations through Steam app manifests and libraryfolders.vdf,
/// with a manual path fallback. It deliberately checks the real AppIDs instead
/// of guessing from the executable name.
/// </summary>
public sealed partial class SteamGameLocator : IGameLocator
{
    private readonly IAppSettingsService _settings;
    private readonly IAppLogger? _logger;

    public SteamGameLocator(IAppSettingsService settings, IAppLogger? logger = null)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<GameInstallation>> DetectAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<GameInstallation>>(() =>
        {
            var installations = new List<GameInstallation>();

            foreach (var root in GetSteamLibraries())
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var profile in KnownGameProfiles.All)
                {
                    var manifest = Path.Combine(root, "steamapps", $"appmanifest_{profile.SteamAppId}.acf");
                    var installDir = ReadInstallDir(manifest);
                    if (string.IsNullOrWhiteSpace(installDir)) continue;

                    var gameDir = Path.Combine(root, "steamapps", "common", installDir);
                    AddIfValid(installations, profile, gameDir);
                }

                // Fallback for portable/repacked installs without an appmanifest.
                var common = Path.Combine(root, "steamapps", "common");
                if (!Directory.Exists(common)) continue;

                foreach (var candidate in Directory.EnumerateDirectories(common))
                {
                    var dataDir = Path.Combine(candidate, KnownGameProfiles.Full.DataDirectoryName);
                    if (!Directory.Exists(dataDir)) continue;

                    var profile = ResolveProfile(candidate);
                    AddIfValid(installations, profile, candidate);
                }
            }

            var manualPath = _settings.Current.GamePath;
            if (!string.IsNullOrWhiteSpace(manualPath))
            {
                var manual = InspectPath(manualPath);
                if (manual is not null) AddIfValid(installations, manual.Profile, manual.GameDirectory);
            }

            // The same directory is usually found more than once (appmanifest
            // plus the common/ fallback, and possibly several registry roots), so
            // collapse duplicates before logging or returning.
            var distinct = installations
                .GroupBy(installation => installation.GameDirectory, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(installation => installation.Profile.Edition).First())
                .OrderByDescending(installation => installation.Profile.Edition)
                .ToList();

            _logger?.Debug($"Game detection finished: {distinct.Count} installation(s) from {installations.Count} candidate(s)");
            foreach (var installation in distinct)
                _logger?.Debug($"Detected {installation.Profile.EditionLabel}: {installation.GameDirectory} (BepInEx={installation.HasBepInEx})");

            return distinct;
        }, cancellationToken);
    }

    public Task<GameInstallation?> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => InspectPath(path), cancellationToken);
    }

    private static GameInstallation? InspectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var fullPath = Path.GetFullPath(path);
        var directory = Directory.Exists(fullPath)
            ? fullPath
            : File.Exists(fullPath)
                ? Path.GetDirectoryName(fullPath)
                : null;

        if (directory is null) return null;

        var dataDirectory = Path.Combine(directory, KnownGameProfiles.Full.DataDirectoryName);
        if (!Directory.Exists(dataDirectory)) return null;

        var profile = ResolveProfile(directory);
        var executable = FindExecutable(directory);

        return new GameInstallation(
            profile,
            directory,
            executable,
            HasBepInEx(directory),
            Directory.Exists(Path.Combine(directory, "BepInEx", "plugins")));
    }

    private static void AddIfValid(
        ICollection<GameInstallation> installations,
        GameProfile profile,
        string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return;

        gameDirectory = NormalizeDirectory(gameDirectory);
        var dataDirectory = Path.Combine(gameDirectory, profile.DataDirectoryName);
        if (!Directory.Exists(dataDirectory)) return;

        installations.Add(new GameInstallation(
            profile,
            gameDirectory,
            FindExecutable(gameDirectory),
            HasBepInEx(gameDirectory),
            Directory.Exists(Path.Combine(gameDirectory, "BepInEx", "plugins"))));
    }

    private static GameProfile ResolveProfile(string gameDirectory)
    {
        var parent = Directory.GetParent(gameDirectory)?.FullName;
        if (parent is not null)
        {
            // gameDirectory is normally steamapps/common/<installDir>.
            var steamapps = Directory.GetParent(parent)?.FullName;
            if (steamapps is not null)
            {
                if (File.Exists(Path.Combine(steamapps, $"appmanifest_{KnownGameProfiles.Demo.SteamAppId}.acf")))
                {
                    var demoDir = ReadInstallDir(Path.Combine(steamapps, $"appmanifest_{KnownGameProfiles.Demo.SteamAppId}.acf"));
                    if (string.Equals(demoDir, Path.GetFileName(gameDirectory), StringComparison.OrdinalIgnoreCase))
                        return KnownGameProfiles.Demo;
                }

                if (File.Exists(Path.Combine(steamapps, $"appmanifest_{KnownGameProfiles.Full.SteamAppId}.acf")))
                    return KnownGameProfiles.Full;
            }
        }

        return Path.GetFileName(gameDirectory).Contains("demo", StringComparison.OrdinalIgnoreCase)
            ? KnownGameProfiles.Demo
            : KnownGameProfiles.Full;
    }

    private static string? FindExecutable(string gameDirectory)
    {
        try
        {
            var executables = Directory.EnumerateFiles(gameDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Contains("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return executables.FirstOrDefault(path =>
                       Path.GetFileName(path).Contains("Casualties", StringComparison.OrdinalIgnoreCase)
                       || Path.GetFileName(path).Contains("Unknown", StringComparison.OrdinalIgnoreCase))
                   ?? executables.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool HasBepInEx(string gameDirectory) =>
        Directory.Exists(Path.Combine(gameDirectory, "BepInEx"))
        && File.Exists(Path.Combine(gameDirectory, "winhttp.dll"))
        && File.Exists(Path.Combine(gameDirectory, ".doorstop_version"));

    private static IReadOnlyList<string> GetSteamLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            // InstallPath first: HKCU SteamPath is typically stored lower-cased
            // with forward slashes (d:/program files (x86)/steam), so letting
            // the properly cased value win keeps displayed/saved paths tidy.
            foreach (var value in new[] { "InstallPath", "SteamPath" })
            {
                var path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", value, null) as string;
                AddRoot(roots, path);

                path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", value, null) as string;
                AddRoot(roots, path);
            }

            foreach (var defaultPath in new[]
                     {
                         @"C:\Program Files (x86)\Steam",
                         @"C:\Program Files\Steam"
                     })
            {
                if (Directory.Exists(defaultPath)) AddRoot(roots, defaultPath);
            }
        }

        foreach (var root in roots.ToList())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            try
            {
                foreach (Match match in LibraryPathRegex().Matches(File.ReadAllText(vdf)))
                {
                    var path = match.Groups["path"].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(path)) AddRoot(roots, path);
                }
            }
            catch
            {
                // A malformed VDF should not prevent manual game-path use.
            }
        }

        return roots.Where(Directory.Exists).ToList();
    }

    private static void AddRoot(ISet<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            roots.Add(NormalizeDirectory(path));
        }
        catch
        {
            // An unusable registry/VDF value is skipped, not fatal.
        }
    }

    /// <summary>Canonical directory form for comparisons: full path, native
    /// separators, no trailing separator (drive roots keep theirs).</summary>
    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string? ReadInstallDir(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            var match = InstallDirRegex().Match(File.ReadAllText(manifestPath));
            return match.Success ? match.Groups["dir"].Value : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex("\"path\"\\s*\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex("\"installdir\"\\s*\"(?<dir>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();
}
