using ModificationsKnown.Core.Common;

namespace ModificationsKnown.Core.Games;

public interface IGameLauncher
{
    /// <summary>Launches through Steam using the profile's real AppID.</summary>
    Task<OperationResult> LaunchViaSteamAsync(GameProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Launches a local executable directly.</summary>
    Task<OperationResult> LaunchDirectAsync(GameInstallation installation, CancellationToken cancellationToken = default);

    Task<OperationResult> OpenGameFolderAsync(GameInstallation installation, CancellationToken cancellationToken = default);
}
