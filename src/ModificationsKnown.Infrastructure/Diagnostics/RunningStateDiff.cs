namespace ModificationsKnown.Infrastructure.Diagnostics;

/// <summary>
/// Compares two running-process snapshots by installation directory.
/// Only installations with a positive process count belong to the running
/// state: an idle scan that sees every installation at zero must compare equal
/// to an empty snapshot instead of reporting a change every polling tick.
/// Keys are compared case-insensitively; zero or negative counts are treated
/// as absent, so stale zero entries in either snapshot are ignored.
/// </summary>
public static class RunningStateDiff
{
    public static bool HasChanged(
        IReadOnlyDictionary<string, int> activeCounts,
        IReadOnlyDictionary<string, int> previousCounts)
    {
        var active = RunningOnly(activeCounts);
        var previous = RunningOnly(previousCounts);

        if (active.Count != previous.Count) return true;

        foreach (var pair in active)
        {
            if (!previous.TryGetValue(pair.Key, out var count) || count != pair.Value)
                return true;
        }

        return false;
    }

    private static Dictionary<string, int> RunningOnly(IReadOnlyDictionary<string, int> counts)
    {
        var running = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in counts)
        {
            if (pair.Value > 0) running[pair.Key] = pair.Value;
        }

        return running;
    }
}
