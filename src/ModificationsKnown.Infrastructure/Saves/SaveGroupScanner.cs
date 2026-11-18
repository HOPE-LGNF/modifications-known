using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// Rebuilds group identity from the manifests inside the profile directories.
/// This is the recovery path when the index is lost and the foundation for
/// re-discovering groups restored from the Windows recycle bin.
/// </summary>
public sealed class SaveGroupScanner
{
    private readonly string _profilesRoot;
    private readonly IAppLogger? _logger;

    public SaveGroupScanner(string profilesRoot, IAppLogger? logger = null)
    {
        _profilesRoot = profilesRoot;
        _logger = logger;
    }

    public IReadOnlyList<SaveGroup> Scan(string installationKey)
    {
        var directory = Path.Combine(_profilesRoot, installationKey);
        if (!Directory.Exists(directory)) return [];

        var groups = new List<SaveGroup>();
        foreach (var groupDirectory in Directory.EnumerateDirectories(directory))
        {
            var folderName = Path.GetFileName(groupDirectory);
            if (!Guid.TryParseExact(folderName, "N", out var folderId))
            {
                // The legacy single profile had data directly in this directory;
                // only GUID folders are groups.
                continue;
            }

            if (!SaveGroupManifestStore.TryRead(groupDirectory, out var manifest))
            {
                _logger?.Warn($"跳过损坏或不受支持的存档组清单：{groupDirectory}");
                continue;
            }

            if (manifest.GroupId != folderId
                || !string.Equals(manifest.InstallationKey, installationKey, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Warn($"存档组清单与目录或安装身份不匹配：{groupDirectory}");
                continue;
            }

            groups.Add(new SaveGroup(
                manifest.GroupId,
                manifest.Name.Trim(),
                manifest.CreatedAt,
                manifest.IsDefault,
                groupDirectory));
        }

        return groups
            .OrderByDescending(group => group.IsDefault)
            .ThenBy(group => group.CreatedAt)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
