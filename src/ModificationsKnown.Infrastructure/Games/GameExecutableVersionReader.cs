using System.Diagnostics;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.Infrastructure.Games;

/// <summary>
/// Reads the version stamped into an installation's executable. Missing paths,
/// unreadable files and version-less binaries all yield null so the UI can show
/// “未知版本” instead of a guess. The file query is injectable so the logic can
/// be tested without a real game executable.
/// </summary>
public sealed class GameExecutableVersionReader
{
    private readonly Func<string, string?> _readFileVersion;

    public GameExecutableVersionReader(Func<string, string?>? readFileVersion = null) =>
        _readFileVersion = readFileVersion ?? ReadFileVersion;

    public string? ReadVersion(GameInstallation installation) =>
        ReadVersion(installation.ExecutablePath);

    public string? ReadVersion(string? executablePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                return null;

            var version = _readFileVersion(executablePath);
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch
        {
            // A locked or malformed executable is simply an unknown version.
            return null;
        }
    }

    private static string? ReadFileVersion(string path) =>
        FileVersionInfo.GetVersionInfo(path).FileVersion;
}
