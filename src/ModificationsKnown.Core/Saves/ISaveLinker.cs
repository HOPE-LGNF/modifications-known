namespace ModificationsKnown.Core.Saves;

/// <summary>
/// Junction operations used before a launch. Abstracted so tests can run on
/// temporary directories without touching a real LocalLow profile.
/// </summary>
public interface ISaveLinker
{
    /// <summary>False on platforms without NTFS junctions (save isolation is
    /// then skipped). Tests use a fake implementation and report true.</summary>
    bool IsSupported { get; }

    bool Exists(string path);

    /// <summary>True only when the path itself is a link; target is absolute.</summary>
    bool TryGetTarget(string path, out string target);

    bool CreateJunction(string linkPath, string targetPath, out string error);

    /// <summary>Removes the link only; the target directory keeps its files.</summary>
    bool RemoveJunction(string linkPath, out string error);
}
