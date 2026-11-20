using System.Text.RegularExpressions;
using ModificationsKnown.Core.Common;

namespace ModificationsKnown.Core.Nexus;

/// <summary>Nexus file versions are often just "v1" or "v2"; the semantic mod
/// version may only appear in the file/version name (e.g. "1.8.3-v1").</summary>
public static class NexusVersionText
{
    private static readonly Regex VersionPattern = new(
        @"\d{1,4}(?:\.\d{1,4}){1,3}(?:[-_][A-Za-z0-9]+)*",
        RegexOptions.Compiled);

    public static string Resolve(NexusFileVersion version, NexusModFile? file = null)
    {
        foreach (var candidate in new[] { version.Version, version.Name, file?.Name })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var match = VersionPattern.Match(candidate);
            if (match.Success) return match.Value;
        }

        return string.IsNullOrWhiteSpace(version.Version) ? version.Name : version.Version;
    }

    public static string Label(NexusFileVersion version, NexusModFile? file = null) =>
        VersionText.Label(Resolve(version, file));
}
