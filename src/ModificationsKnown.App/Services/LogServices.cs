using ModificationsKnown.Core.Diagnostics.Logs;
using ModificationsKnown.Infrastructure.Diagnostics.Logs;

namespace ModificationsKnown.App.Services;

/// <summary>Describes the log sources the page can offer. Kept behind an
/// interface so view-model tests never touch the real game folders.</summary>
public interface ILogSourceProvider
{
    IReadOnlyList<LogSource> Describe();
}

public sealed class GameLogSourceProvider : ILogSourceProvider
{
    private readonly GameContext _game;

    public GameLogSourceProvider(GameContext game) => _game = game;

    public IReadOnlyList<LogSource> Describe()
    {
        var sources = new List<LogSource>();

        if (_game.Selected is { } installation)
        {
            sources.Add(LogSourceLocations.BepInEx(installation.GameDirectory, installation.GameDirectory));
        }

        sources.Add(LogSourceLocations.UnityCurrent());
        sources.Add(LogSourceLocations.UnityPrevious());

        return sources;
    }
}
