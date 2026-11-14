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

    public bool IsRunningPath(string gameDirectory) =>
        _counts.TryGetValue(Normalize(gameDirectory), out var count) && count > 0;

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

        var changed = counts.Count != _counts.Count
                      || counts.Any(pair => !_counts.TryGetValue(pair.Key, out var current) || current != pair.Value);

        if (!changed) return;

        _counts.Clear();
        foreach (var pair in counts)
            if (pair.Value > 0) _counts[pair.Key] = pair.Value;

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
