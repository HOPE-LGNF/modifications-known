namespace ModificationsKnown.Core.Mods;

/// <summary>
/// Outcome of installing one archive. An archive may contain several packages,
/// so success is reported per package rather than as a single boolean.
/// </summary>
public sealed record InstalledPackageResult(
    string PackageName,
    bool Success,
    string Message,
    string? LocalId = null);

public sealed record ModInstallResult(
    bool Success,
    string Message,
    IReadOnlyList<InstalledPackageResult> Packages,
    IReadOnlyList<string>? AuxiliaryFiles = null)
{
    /// <summary>True only when every package committed. <see cref="Success"/>
    /// means at least one package committed, which is not enough for an update.</summary>
    public bool AllSucceeded =>
        Packages.Count > 0 && Packages.All(package => package.Success);

    public static ModInstallResult Failure(string message) => new(false, message, []);
}
