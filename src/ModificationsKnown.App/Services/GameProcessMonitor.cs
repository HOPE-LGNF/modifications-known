using System.Collections.Concurrent;
using System.Diagnostics;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Diagnostics;

namespace ModificationsKnown.App.Services;

/// <summary>
/// Polls running processes and maps them to installations. Matching (path first,
/// then process name) lives in <see cref="GameProcessProbe"/> so it can be
/// tested without a UI.
/// <para>
/// The 3-second <see cref="Changed"/> snapshot is for display only. Save
/// operations use <see cref="Probe"/> / <see cref="ProbeAnyRunning"/>, which
/// enumerate processes again at call time and report <c>Unknown</c> rather than
/// "not running" when a process cannot be inspected.
/// </para>
/// </summary>
public sealed class GameProcessMonitor : IDisposable
{
    private readonly GameContext _game;
    private readonly IAppLogger? _logger;
    private readonly GameProcessProbe _probe;
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _scanGate = new();
    private Task? _loop;

    public GameProcessMonitor(GameContext game, IAppLogger? logger = null)
    {
        _game = game;
        _logger = logger;
        _probe = new GameProcessProbe(logger);
    }

    public event EventHandler? Changed;

    public void Start()
    {
        if (_loop is not null) return;

        _game.Changed += (_, _) => Scan();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public bool IsRunning(GameInstallation installation) =>
        Count(installation) > 0;

    /// <summary>True while any registered installation has a live process.
    /// Save-group writes are locked for every installation in that case: the
    /// game always writes the one global LocalLow directory, whichever
    /// installation owns the running process.</summary>
    public bool AnyRunning => _counts.Values.Any(count => count > 0);

    public int Count(GameInstallation installation) =>
        _counts.TryGetValue(Normalize(installation.GameDirectory), out var count) ? count : 0;

    /// <summary>
    /// Fresh check for one installation, used right before a save write. It
    /// never reads the cached snapshot, so a game started from Steam since the
    /// last poll is still detected.
    /// </summary>
    public GameRunningState Probe(GameInstallation installation)
    {
        lock (_scanGate)
        {
            return _probe.Probe(Target(installation));
        }
    }

    /// <summary>Fresh check across every installation and every known executable
    /// name. A game launched outside MK (including a copy in an unregistered
    /// folder) still locks all save writes.</summary>
    public GameRunningState ProbeAnyRunning()
    {
        lock (_scanGate)
        {
            return _probe.ProbeAnyRunning(Targets());
        }
    }

    /// <summary>Fast poll used right after a launch command: scans every
    /// 350 ms until the instance appears or the timeout expires.</summary>
    public async Task<bool> WaitForRunningAsync(
        GameInstallation installation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            Scan();
            if (IsRunning(installation)) return true;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return IsRunning(installation);
    }

    public bool IsRunningPath(string gameDirectory) =>
        _counts.TryGetValue(Normalize(gameDirectory), out var count) && count > 0;

    /// <summary>Closes every process mapped to this installation. Tries a
    /// graceful window close first, then kills the process tree. Steam has no
    /// supported “stop game” URL, so this is the reliable route.</summary>
    public async Task<int> CloseInstanceAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        var pids = _probe.FindProcessIds(Target(installation));
        var closed = 0;

        foreach (var pid in pids)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) continue;

                process.CloseMainWindow();
                if (!await WaitForExitAsync(process, TimeSpan.FromMilliseconds(2500), cancellationToken))
                {
                    process.Kill(entireProcessTree: true);
                    await WaitForExitAsync(process, TimeSpan.FromSeconds(3), cancellationToken);
                }

                closed++;
            }
            catch
            {
                // The process may have exited between enumeration and close.
            }
        }

        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        Scan();
        return closed;
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var exit = process.WaitForExitAsync(cancellationToken);
            var delay = Task.Delay(timeout, cancellationToken);
            return await Task.WhenAny(exit, delay).ConfigureAwait(false) == exit
                   && process.HasExited;
        }
        catch
        {
            return process.HasExited;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Scan();
            }
            catch (Exception exception)
            {
                _logger?.Warn($"进程检测失败：{exception.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Scan()
    {
        lock (_scanGate)
        {
            ScanCore();
        }
    }

    private void ScanCore()
    {
        var installations = _game.Installations.ToList();
        if (installations.Count == 0)
        {
            if (!_counts.IsEmpty)
            {
                _counts.Clear();
                Changed?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        var scan = _probe.Scan(installations.Select(Target).ToList());
        var active = scan.Counts;

        // Only running installations are part of the state; keeping explicit
        // zero entries would make every idle scan look like a change.
        if (!RunningStateDiff.HasChanged(active, _counts)) return;

        _counts.Clear();
        foreach (var pair in active) _counts[pair.Key] = pair.Value;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<GameProcessTarget> Targets() =>
        _game.Installations.Select(Target).ToList();

    private static GameProcessTarget Target(GameInstallation installation) =>
        new(installation.GameDirectory, installation.ExecutablePath);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Shutdown only.
        }

        _cts.Dispose();
    }
}
