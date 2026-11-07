namespace ModificationsKnown.Core.Mods;

/// <summary>
/// Outcome of installing one archive. An archive may contain several packages,
/// so success is reported per package rather than as a single boolean.
/// </summary>
public sealed record InstalledPackageResult(string PackageName, bool Success, string Message);

public sealed record ModInstallResult(
    bool Success,
    string Message,
    IReadOnlyList<InstalledPackageResult> Packages)
{
    public static ModInstallResult Failure(string message) => new(false, message, []);
}
