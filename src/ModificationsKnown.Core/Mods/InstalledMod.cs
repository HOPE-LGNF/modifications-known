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

    public bool CanDelete { get; init; }

    public bool IsEnabled => Plugins.Any(plugin => plugin.IsEnabled);

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
