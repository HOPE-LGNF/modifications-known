using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.App.Services;

public partial class GameContext : ObservableObject
{
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

    public async Task RefreshAsync(IGameLocator locator, string? preferredId = null, CancellationToken cancellationToken = default)
    {
        IsDetecting = true;
        try
        {
            var detected = await locator.DetectAsync(cancellationToken);
            var previous = preferredId ?? Selected?.Profile.Id;

            Installations.Clear();
            foreach (var installation in detected)
                Installations.Add(installation);

            Selected = Installations.FirstOrDefault(installation => installation.Profile.Id == previous)
                       ?? Installations.FirstOrDefault();
        }
        finally
        {
            IsDetecting = false;
        }
    }
}
