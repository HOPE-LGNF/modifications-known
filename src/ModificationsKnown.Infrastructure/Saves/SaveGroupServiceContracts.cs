using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Saves;

public sealed class SaveGroupOptions
{
    public string ProfilesRoot { get; init; } = AppPaths.SaveProfilesDirectory;

    public string CatalogPath { get; init; } = Path.Combine(AppPaths.RootDirectory, SaveGroupFormat.CatalogFileName);

    public string BackupsRoot { get; init; } = Path.Combine(AppPaths.RootDirectory, "save-backups");

    public string? StagingRoot { get; init; }

    public string LiveSaveDirectory { get; init; } = ResolveLiveSaveDirectory();

    private static string ResolveLiveSaveDirectory() =>
        AppPaths.AcceptanceRoot is { } acceptanceRoot
            ? Path.Combine(acceptanceRoot, "LocalLow", "Orsoniks", "CasualtiesUnknown")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Orsoniks", "CasualtiesUnknown");

    public ISaveLinker Linker { get; init; } = new WindowsJunctionLinker();

    public IRecycleBin RecycleBin { get; init; } = new WindowsRecycleBin();

    public IAppLogger? Logger { get; init; }

    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Set by the app from the process monitor. Every operation that
    /// writes or copies saves must refuse while any game instance is running.</summary>
    public Func<GameInstallation, bool>? IsGameRunning { get; init; }

    /// <summary>
    /// Fresh, three-state probe used by every mutating operation. It is called
    /// while the save-group write lock is held, so it must scan the process list
    /// again instead of returning a cached monitor snapshot. <c>Unknown</c> is
    /// treated as running (fail closed); when unset, <see cref="IsGameRunning"/>
    /// is used and cannot report <c>Unknown</c>.
    /// </summary>
    public Func<GameInstallation, GameRunningState>? ProbeGameRunningState { get; init; }
}

public enum SaveGroupInitializationState
{
    Ready,
    NeedsUnmanagedChoice,
    Cancelled,
    Failed
}

public enum UnmanagedSaveAction
{
    Import,
    CreateEmpty
}

/// <summary>What the unmanaged-save dialog decided. <c>OpenInstanceSettings</c>
/// means the receiving copy is not registered yet; nothing was written.</summary>
public enum UnmanagedSaveChoice
{
    Import,
    CreateEmpty,
    OpenInstanceSettings
}

public sealed record UnmanagedSaveCandidate(
    string InstallationKey,
    string DisplayName,
    string GameDirectory,
    int GroupCount)
{
    public string GroupCountLabel => GroupCount == 0
        ? "0 组 · 将新建「默认」"
        : $"{GroupCount} 组 · 将新建「导入的存档」";

    public bool CanCreateEmpty => GroupCount == 0;
}

public sealed record UnmanagedSaveDecision(
    UnmanagedSaveChoice Choice,
    string? TargetInstallationKey = null);

public sealed record SaveGroupInitializationResult(
    SaveGroupInitializationState State,
    string Message,
    IReadOnlyList<SaveGroup> Groups,
    Guid? NextLaunchGroupId,
    string LiveDirectory,
    int UnmanagedFileCount = 0)
{
    public bool Success => State == SaveGroupInitializationState.Ready;
}

public sealed record SaveLaunchResult(
    bool Success,
    string Message,
    SaveGroup? Group = null,
    bool Switched = false);

public sealed record SaveLaunchPreview(
    bool NeedsInitialization,
    SaveGroup? Group,
    bool NeedsSwitch,
    string LiveDirectory);

/// <summary>Outcome of aligning the live junction with the group an
/// installation would use next (active-instance change / startup).</summary>
public enum SaveGroupActivationState
{
    Switched,
    AlreadyActive,
    NeedsUnmanagedChoice,
    RunningLocked,
    Failed
}

public sealed record SaveGroupActivationResult(
    SaveGroupActivationState State,
    string Message,
    SaveGroup? Group = null,
    string LiveDirectory = "",
    int UnmanagedFileCount = 0)
{
    public bool Success =>
        State is SaveGroupActivationState.Switched or SaveGroupActivationState.AlreadyActive;
}

public sealed record SaveGroupOperationResult(
    bool Success,
    string Message,
    SaveGroup? Group = null);

/// <summary>Which installation owns the group the live junction points at.
/// The game writes one global LocalLow directory, so this is global state that
/// every installation row on the saves page needs to describe.</summary>
public sealed record SaveGroupOwner(string InstallationKey, SaveGroup Group, string GameDirectory);
