namespace ModificationsKnown.Core.Mods;

/// <summary>Controls the narrow archive layouts the installer may accept.
/// Auxiliary files are only enabled for a known update target, so a generic
/// archive still cannot silently scatter files into plugins/ or patchers/.</summary>
public sealed record ArchiveInstallOptions(
    string? TargetPackageName = null,
    bool AllowAuxiliaryFiles = false);
