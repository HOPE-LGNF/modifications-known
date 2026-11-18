using System.Diagnostics;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Diagnostics;

/// <summary>One installation as the process probe needs it.</summary>
public sealed record GameProcessTarget(string GameDirectory, string? ExecutablePath);

/// <summary>Running process counts per normalized game directory plus a flag
/// for processes whose name and path could not be read (fail closed).</summary>
public sealed record GameProcessScan(IReadOnlyDictionary<string, int> Counts, bool Uncertain);

/// <summary>
/// Maps live processes to game installations. The game always writes the one
/// global LocalLow directory, so a process is treated as the game when either
/// <list type="bullet">
/// <item>its module path is the registered executable or under a registered game
/// directory, or</item>
/// <item>its process name matches the game executable — even when the path is
/// readable but points somewhere unregistered (a copy in a temp folder still
/// writes the same saves).</item>
/// </list>
/// A process whose name cannot be read makes the result <see cref="GameRunningState.Unknown"/>:
/// "cannot tell" must never be reported as "not running".
/// </summary>
public sealed class GameProcessProbe
{
    /// <summary>Executable names the game ships with.</summary>
    public static readonly string[] KnownProcessNames = ["CasualtiesUnknown"];

    private readonly IAppLogger? _logger;

    public GameProcessProbe(IAppLogger? logger = null) => _logger = logger;

    public GameRunningState ProbeAnyRunning(IReadOnlyList<GameProcessTarget> targets) =>
        Merge(Scan(targets));

    public GameRunningState Probe(GameProcessTarget target) => Merge(Scan([target]));

    /// <summary>
    /// Enumerates processes once and counts matches per target. When the process
    /// list itself cannot be read the scan reports <c>Uncertain</c> with no
    /// counts, which callers must treat as "game may be running".
    /// </summary>
    public GameProcessScan Scan(IReadOnlyList<GameProcessTarget> targets)
    {
        var entries = targets.Select(CreateEntry).ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) counts[entry.Root] = 0;

        if (!TryGetProcesses(out var processes, out var enumerationError))
        {
            _logger?.Warn($"无法枚举进程：{enumerationError}");
            return new GameProcessScan(Empty, Uncertain: true);
        }

        var uncertain = false;
        try
        {
            foreach (var process in processes)
            {
                var index = Classify(process, entries, out var readFailure);
                uncertain |= readFailure;
                if (index >= 0) counts[entries[index].Root]++;
            }
        }
        catch (Exception exception)
        {
            _logger?.Warn($"检测游戏进程失败：{exception.Message}");
            return new GameProcessScan(Empty, Uncertain: true);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        if (uncertain) _logger?.Warn("有进程无法读取名称或路径；按“游戏可能正在运行”处理。");

        var active = counts
            .Where(pair => pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new GameProcessScan(active, uncertain);
    }

    /// <summary>Pids whose module path matches the target exactly or lives under
    /// its game directory. Process names are not used here: closing a process by
    /// name could kill an unrelated copy.</summary>
    public IReadOnlyList<int> FindProcessIds(GameProcessTarget target)
    {
        var entry = CreateEntry(target);
        var result = new List<int>();

        if (!TryGetProcesses(out var processes, out _)) return result;

        try
        {
            foreach (var process in processes)
            {
                var path = TryGetModulePath(process);
                if (string.IsNullOrWhiteSpace(path)) continue;

                var normalized = Normalize(path);
                if ((entry.Exe is not null
                     && string.Equals(normalized, entry.Exe, StringComparison.OrdinalIgnoreCase))
                    || normalized.StartsWith(entry.Root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(process.Id);
                }
            }
        }
        catch (Exception exception)
        {
            _logger?.Warn($"枚举游戏进程失败：{exception.Message}");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return result;
    }

    private static GameRunningState Merge(GameProcessScan scan)
    {
        if (scan.Counts.Values.Any(count => count > 0)) return GameRunningState.Running;
        return scan.Uncertain ? GameRunningState.Unknown : GameRunningState.NotRunning;
    }

    /// <summary>Returns the target index for a process, or -1 when it is not the
    /// game. <paramref name="readFailure"/> means name and path were both
    /// unreadable, so the caller cannot rule the process out.</summary>
    private static int Classify(Process process, IReadOnlyList<Entry> entries, out bool readFailure)
    {
        readFailure = false;
        var path = TryGetModulePath(process);
        var name = TryGetProcessName(process);

        if (!string.IsNullOrWhiteSpace(path))
        {
            var normalized = Normalize(path);
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if ((entry.Exe is not null
                     && string.Equals(normalized, entry.Exe, StringComparison.OrdinalIgnoreCase))
                    || normalized.StartsWith(entry.Root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            // The path was readable but belongs to no registered directory.
            // A known game executable name still counts: that copy writes the
            // same global LocalLow directory.
            return name is null ? -1 : FindByName(entries, name);
        }

        if (name is null)
        {
            readFailure = true;
            return -1;
        }

        return FindByName(entries, name);
    }

    private static int FindByName(IReadOnlyList<Entry> entries, string processName)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var exeName = entries[index].ExeName;
            if (exeName is not null
                && string.Equals(exeName, processName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        // An unregistered copy of the game: attribute it to the first target so
        // the UI shows a running state (the save lock is global anyway).
        if (entries.Count > 0 && KnownProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
            return 0;

        return -1;
    }

    private static Entry CreateEntry(GameProcessTarget target) => new(
        Normalize(target.GameDirectory),
        string.IsNullOrWhiteSpace(target.ExecutablePath) ? null : Normalize(target.ExecutablePath),
        string.IsNullOrWhiteSpace(target.ExecutablePath)
            ? null
            : Path.GetFileNameWithoutExtension(target.ExecutablePath));

    private static bool TryGetProcesses(out Process[] processes, out string? error)
    {
        try
        {
            processes = Process.GetProcesses();
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            processes = [];
            error = exception.Message;
            return false;
        }
    }

    private static string? TryGetModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            // Elevated/other-session processes expose no module path.
            return null;
        }
    }

    private static string? TryGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static IReadOnlyDictionary<string, int> Empty { get; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Entry(string Root, string? Exe, string? ExeName);
}
