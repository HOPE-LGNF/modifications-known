using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.App.Services;

public partial class GameContext : ObservableObject
{
    private readonly GameInstanceStore _instances;

    public GameContext(GameInstanceStore instances)
    {
        _instances = instances;
    }

    [ObservableProperty]
    private GameInstallation? _selected;

    [ObservableProperty]
    private bool _isDetecting;

    public ObservableCollection<GameInstallation> Installations { get; } = [];

    public bool HasGame => Selected is not null;

    public bool HasBepInEx => Selected?.HasBepInEx == true;

    public event EventHandler? Changed;

    partial void OnSelectedChanged(GameInstallation? value)
    {
        OnPropertyChanged(nameof(HasGame));
        OnPropertyChanged(nameof(HasBepInEx));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Loads the saved instance list. Only the very first run (empty list) falls
    /// back to a Steam scan; afterwards startup never re-detects, so an instance
    /// the user removed stays removed.
    /// </summary>
    public async Task InitializeAsync(
        IGameLocator locator,
        string? preferredPath = null,
        string? preferredId = null,
        CancellationToken cancellationToken = default)
    {
        IsDetecting = true;
        try
        {
            if (!_instances.Exists)
            {
                await RediscoverCoreAsync(locator, preferredPath, preferredId, cancellationToken);
                return;
            }

            Populate(_instances.LoadInstances(), preferredPath, preferredId);
        }
        finally
        {
            IsDetecting = false;
        }
    }

    /// <summary>Explicit re-detection (top-bar refresh / settings “重新探测”).
    /// Existing aliases win, new paths are added, and entries that were not
    /// detected this time are kept until the user removes them.</summary>
    public async Task RefreshAsync(
        IGameLocator locator,
        string? preferredPath = null,
        string? preferredId = null,
        CancellationToken cancellationToken = default)
    {
        IsDetecting = true;
        try
        {
            await RediscoverCoreAsync(locator, preferredPath, preferredId, cancellationToken);
        }
        finally
        {
            IsDetecting = false;
        }
    }

    private async Task RediscoverCoreAsync(
        IGameLocator locator,
        string? preferredPath,
        string? preferredId,
        CancellationToken cancellationToken)
    {
        var detected = await locator.DetectAsync(cancellationToken);
        foreach (var installation in detected)
        {
            var alias = _instances.GetAlias(installation.GameDirectory);
            _instances.Upsert(installation, alias);
        }

        Populate(_instances.LoadInstances(), preferredPath, preferredId);
    }

    /// <summary>Adds one user-picked executable/directory without touching the
    /// other instances.</summary>
    public async Task<GameInstallation?> AddPathAsync(
        string path,
        IGameLocator locator,
        CancellationToken cancellationToken = default)
    {
        var installation = await locator.InspectAsync(path, cancellationToken);
        if (installation is null) return null;

        _instances.Upsert(installation);
        var saved = _instances.Get(installation.GameDirectory);
        if (saved is null) return null;

        var built = Build(saved);
        var existing = Installations.FirstOrDefault(item => PathEquals(item.GameDirectory, built.GameDirectory));
        if (existing is not null)
            Installations[Installations.IndexOf(existing)] = built;
        else
            Installations.Add(built);

        Selected = built;
        return built;
    }

    private void Populate(
        IReadOnlyList<SavedGameInstance> saved,
        string? preferredPath,
        string? preferredId)
    {
        var previousPath = preferredPath ?? Selected?.GameDirectory;
        var previousId = preferredId ?? Selected?.Profile.Id;

        Installations.Clear();
        foreach (var record in saved)
        {
            try
            {
                Installations.Add(Build(record));
            }
            catch
            {
                // A single unreadable record must not hide the others.
            }
        }

        Selected = Installations.FirstOrDefault(installation =>
                       previousPath is not null && PathEquals(installation.GameDirectory, previousPath))
                   ?? Installations.FirstOrDefault(installation => installation.Profile.Id == previousId)
                   ?? Installations.FirstOrDefault();
    }

    private static GameInstallation Build(SavedGameInstance saved)
    {
        var profile = KnownGameProfiles.All.FirstOrDefault(candidate => candidate.Id == saved.ProfileId)
                      ?? KnownGameProfiles.All.FirstOrDefault(candidate => candidate.SteamAppId == saved.SteamAppId);

        if (profile is null)
        {
            Enum.TryParse<GameEdition>(saved.Edition, ignoreCase: true, out var edition);
            profile = new GameProfile(
                saved.ProfileId,
                edition == GameEdition.Demo ? "未知伤亡（试玩版）" : "未知伤亡",
                "CU",
                edition,
                saved.SteamAppId,
                "scavprototype",
                "CasualtiesUnknown_Data");
        }

        var bepInEx = Path.Combine(saved.Path, "BepInEx");
        return new GameInstallation(
            profile,
            saved.Path,
            saved.ExecutablePath,
            Directory.Exists(bepInEx),
            Directory.Exists(Path.Combine(bepInEx, "plugins")))
        {
            Alias = saved.Alias
        };
    }

    public bool SelectByPath(string gameDirectory)
    {
        var match = Installations.FirstOrDefault(installation =>
            PathEquals(installation.GameDirectory, gameDirectory));
        if (match is null) return false;
        Selected = match;
        return true;
    }

    public bool Remove(string gameDirectory)
    {
        var match = Installations.FirstOrDefault(installation =>
            PathEquals(installation.GameDirectory, gameDirectory));
        if (match is null) return false;

        _instances.Remove(gameDirectory);
        var wasSelected = ReferenceEquals(Selected, match);
        Installations.Remove(match);
        if (wasSelected)
        {
            Selected = Installations.FirstOrDefault();
            if (Selected is null) Changed?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    public void Rename(string gameDirectory, string alias)
    {
        _instances.SetAlias(gameDirectory, alias);
        for (var index = 0; index < Installations.Count; index++)
        {
            if (!PathEquals(Installations[index].GameDirectory, gameDirectory))
                continue;

            var updated = Installations[index] with { Alias = alias.Trim() };
            var wasSelected = Selected is not null && PathEquals(Selected.GameDirectory, gameDirectory);
            Installations[index] = updated;
            if (wasSelected) Selected = updated;
            return;
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
