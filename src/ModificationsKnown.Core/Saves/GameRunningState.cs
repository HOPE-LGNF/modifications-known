namespace ModificationsKnown.Core.Saves;

/// <summary>
/// Result of checking whether the game currently holds the live save directory.
/// <see cref="Unknown"/> means the process list or a process module could not be
/// read, so the caller must behave as if the game were running: a save switch is
/// never worth a corrupted profile.
/// </summary>
public enum GameRunningState
{
    NotRunning,
    Running,
    Unknown
}
