namespace ModificationsKnown.Core.Games;

public interface IGameLocator
{
    /// <summary>
    /// Detects all known CU installations. A machine may have the demo, the
    /// full game, both, or neither.
    /// </summary>
    Task<IReadOnlyList<GameInstallation>> DetectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inspects a user-selected executable or game directory.
    /// </summary>
    Task<GameInstallation?> InspectAsync(string path, CancellationToken cancellationToken = default);
}
