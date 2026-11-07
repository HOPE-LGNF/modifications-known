namespace ModificationsKnown.Core.Mods;

/// <summary>
/// One DLL inside an installed package. A package may contain several plugins
/// plus dependency-only assemblies.
/// </summary>
public sealed record InstalledPlugin(
    string FilePath,
    string FileName,
    string? Guid,
    string? Name,
    string? Version,
    bool IsEnabled,
    IReadOnlyList<ModDependency> Dependencies)
{
    public string StateLabel => IsEnabled ? "已启用" : "已禁用";
}
