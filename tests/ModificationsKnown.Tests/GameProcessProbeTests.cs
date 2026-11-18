using System.Diagnostics;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Diagnostics;

namespace ModificationsKnown.Tests;

/// <summary>
/// Regression: a procnamed copy of the game outside every registered game
/// directory still writes the same global LocalLow profile. A readable module
/// path that matches no installation must therefore fall through to the process
/// name, not be treated as "not the game".
/// </summary>
public sealed class GameProcessProbeTests
{
    [Fact]
    public void ProbeAnyRunning_DetectsAGameNamedProcessInAnUnregisteredDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var fake = FakeGameProcess.Start();
        var registered = Path.Combine(Path.GetTempPath(), "mk-probe-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(registered);

        try
        {
            var probe = new GameProcessProbe();
            var targets = new[]
            {
                new GameProcessTarget(registered, Path.Combine(registered, "OtherGame.exe"))
            };

            Assert.Equal(GameRunningState.Running, probe.ProbeAnyRunning(targets));

            var scan = probe.Scan(targets);
            Assert.True(scan.Counts.TryGetValue(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered)), out var count));
            Assert.Equal(1, count);
            Assert.False(scan.Uncertain);
        }
        finally
        {
            Directory.Delete(registered, recursive: true);
        }
    }

    [Fact]
    public void ProbeAnyRunning_DoesNotFlagUnrelatedProcesses()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (IsRealGameRunning()) return;

        var registered = Path.Combine(Path.GetTempPath(), "mk-probe-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(registered);

        try
        {
            var probe = new GameProcessProbe();
            var targets = new[]
            {
                new GameProcessTarget(registered, Path.Combine(registered, "DefinitelyNotTheGame.exe"))
            };

            Assert.Equal(GameRunningState.NotRunning, probe.ProbeAnyRunning(targets));
        }
        finally
        {
            Directory.Delete(registered, recursive: true);
        }
    }

    private static bool IsRealGameRunning() =>
        Process.GetProcesses().Any(process =>
        {
            try
            {
                return string.Equals(process.ProcessName, "CasualtiesUnknown", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
            finally
            {
                process.Dispose();
            }
        });

    /// <summary>A live process named like the game executable, started from a
    /// throwaway directory. cmd.exe keeps running while ping runs.</summary>
    private sealed class FakeGameProcess : IDisposable
    {
        private readonly string _directory;
        private readonly Process _process;

        private FakeGameProcess(string directory, Process process)
        {
            _directory = directory;
            _process = process;
        }

        public static FakeGameProcess Start()
        {
            var directory = Path.Combine(Path.GetTempPath(), "mk-fake-game-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var executable = Path.Combine(directory, "CasualtiesUnknown.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);

            var start = new ProcessStartInfo(executable, "/c ping -n 20 127.0.0.1 > nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动假进程。");
            process.StandardOutput.ReadToEndAsync();
            return new FakeGameProcess(directory, process);
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
            finally
            {
                _process.Dispose();
            }

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // The executable may still be mapped for a moment.
            }
        }
    }
}
