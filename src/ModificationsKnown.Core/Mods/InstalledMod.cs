namespace ModificationsKnown.Core.Mods;

/// <summary>
/// An installed BepInEx package. The package is the unit the launcher enables,
/// disables and deletes, not an individual DLL. This keeps multi-DLL mods and
/// dependency assemblies together.
/// </summary>
public sealed class InstalledMod
{
    public required string PackageId { get; init; }

    public required string DirectoryPath { get; init; }

    public required IReadOnlyList<InstalledPlugin> Plugins { get; init; }

    public PackageManifest? Manifest { get; init; }

    public PackageManifestState ManifestState { get; init; } = PackageManifestState.Unmanaged;

    public string? ManifestError { get; init; }

    public IReadOnlyList<string> MissingFiles { get; init; } = [];

    public IReadOnlyList<string> ModifiedFiles { get; init; } = [];

    public IReadOnlyList<string> UnexpectedFiles { get; init; } = [];

    public bool IsManaged => ManifestState == PackageManifestState.Managed && Manifest is not null;

    /// <summary>Normal uninstall is available only for manifest-managed packages.</summary>
    public bool CanDelete => IsManaged;

    /// <summary>Deleting the whole directory is a separate, explicit action.</summary>
    public bool CanDeleteWhole { get; init; } = true;

    /// <summary>An unmanaged package can be converted into a managed package by writing a baseline manifest.</summary>
    public bool CanAdopt { get; init; }

    public bool IsEnabled => Plugins.Any(plugin => plugin.IsEnabled);

    public bool HasMixedEnabledState =>
        Plugins.Count > 0 && Plugins.Any(plugin => plugin.IsEnabled) && Plugins.Any(plugin => !plugin.IsEnabled);

    public string ManagementLabel => ManifestState switch
    {
        PackageManifestState.Managed => "已托管",
        PackageManifestState.Broken => "清单异常",
        _ => "本地模组"
    };

    public PackageSource? Source => Manifest?.Source;

    public string Name =>
        Plugins.Select(plugin => plugin.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
        ?? PackageId;

    public string? Guid =>
        Plugins.Select(plugin => plugin.Guid)
            .FirstOrDefault(guid => !string.IsNullOrWhiteSpace(guid));

    public string? Version =>
        Plugins.Select(plugin => plugin.Version)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version));

    public int PluginCount => Plugins.Count;

    public IReadOnlyList<ModDependency> Dependencies =>
        Plugins.SelectMany(plugin => plugin.Dependencies)
            .DistinctBy(dependency => dependency.Guid, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string StateLabel => IsEnabled ? "已启用" : "已禁用";

    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? "版本未知" : $"v{Version}";
}
