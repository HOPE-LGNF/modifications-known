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

    public async Task RefreshAsync(
        IGameLocator locator,
        string? preferredPath = null,
        string? preferredId = null,
        CancellationToken cancellationToken = default)
    {
        IsDetecting = true;
        try
        {
            var detected = await locator.DetectAsync(cancellationToken);
            _instances.EnsureAliases(detected);

            var previousPath = preferredPath ?? Selected?.GameDirectory;
            var previousId = preferredId ?? Selected?.Profile.Id;

            Installations.Clear();
            foreach (var installation in detected)
            {
                var alias = _instances.GetAlias(installation.GameDirectory);
                Installations.Add(string.IsNullOrWhiteSpace(alias)
                    ? installation
                    : installation with { Alias = alias });
            }

            Selected = Installations.FirstOrDefault(installation =>
                           string.Equals(installation.GameDirectory, previousPath, StringComparison.OrdinalIgnoreCase))
                       ?? Installations.FirstOrDefault(installation => installation.Profile.Id == previousId)
                       ?? Installations.FirstOrDefault();
        }
        finally
        {
            IsDetecting = false;
        }
    }

    public bool SelectByPath(string gameDirectory)
    {
        var match = Installations.FirstOrDefault(installation =>
            string.Equals(installation.GameDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        Selected = match;
        return true;
    }

    public void Rename(string gameDirectory, string alias)
    {
        _instances.SetAlias(gameDirectory, alias);
        for (var index = 0; index < Installations.Count; index++)
        {
            if (!string.Equals(Installations[index].GameDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            var updated = Installations[index] with { Alias = alias.Trim() };
            var wasSelected = Selected?.GameDirectory.Equals(gameDirectory, StringComparison.OrdinalIgnoreCase) == true;
            Installations[index] = updated;
            if (wasSelected) Selected = updated;
            return;
        }
    }
}
