using System.Collections.Concurrent;
using System.Diagnostics;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.App.Services;

/// <summary>
/// Polls running processes and maps them to installations by executable path
/// (falling back to process names only when a path is unavailable). Multiple
/// instances can run at once; state is tracked per game directory, including a
/// process count for two copies of the same instance.
/// </summary>
public sealed class GameProcessMonitor : IDisposable
{
    private readonly GameContext _game;
    private readonly IAppLogger? _logger;
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _scanGate = new();
    private Task? _loop;

    public GameProcessMonitor(GameContext game, IAppLogger? logger = null)
    {
        _game = game;
        _logger = logger;
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

    public int Count(GameInstallation installation) =>
        _counts.TryGetValue(Normalize(installation.GameDirectory), out var count) ? count : 0;

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
        var pids = FindProcessIds(installation);
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

    private List<int> FindProcessIds(GameInstallation installation)
    {
        var root = Normalize(installation.GameDirectory);
        var exe = string.IsNullOrWhiteSpace(installation.ExecutablePath)
            ? null
            : Normalize(installation.ExecutablePath);

        var result = new List<int>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // Same limitation as detection: no path for elevated processes.
                }

                if (string.IsNullOrWhiteSpace(path)) continue;
                var normalized = Normalize(path);
                if ((exe is not null && string.Equals(normalized, exe, StringComparison.OrdinalIgnoreCase))
                    || normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(process.Id);
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return result;
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

        var counts = installations.ToDictionary(
            installation => Normalize(installation.GameDirectory),
            _ => 0,
            StringComparer.OrdinalIgnoreCase);

        var byExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var installation in installations)
        {
            if (!string.IsNullOrWhiteSpace(installation.ExecutablePath))
                byExe[Normalize(installation.ExecutablePath)] = Normalize(installation.GameDirectory);
        }

        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // Elevated/other-session processes expose no module path.
                }

                string? directory = null;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var normalized = Normalize(path);
                    byExe.TryGetValue(normalized, out directory);
                    directory ??= FindContaining(installations, normalized);
                }

                if (directory is null) continue;
                if (counts.ContainsKey(directory)) counts[directory]++;
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        var runningCounts = counts
            .Where(pair => pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var changed = runningCounts.Count != _counts.Count
                      || runningCounts.Any(pair =>
                          !_counts.TryGetValue(pair.Key, out var current) || current != pair.Value);

        if (!changed) return;

        _counts.Clear();
        foreach (var pair in runningCounts)
            _counts[pair.Key] = pair.Value;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string? FindContaining(IReadOnlyList<GameInstallation> installations, string filePath)
    {
        string? best = null;
        foreach (var installation in installations)
        {
            var root = Normalize(installation.GameDirectory);
            if (!filePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            if (best is null || root.Length > best.Length) best = root;
        }

        return best;
    }

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
