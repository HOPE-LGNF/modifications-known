namespace ModificationsKnown.Core.Mods;

public sealed record ArchivedPackage(string PackageName, string? Version);

public sealed record ArchiveInspection(IReadOnlyList<ArchivedPackage> Packages)
{
    public static ArchiveInspection Empty { get; } = new([]);
}
