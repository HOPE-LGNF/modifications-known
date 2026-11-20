using System.Diagnostics;
using Avalonia.Controls;
using ModificationsKnown.Core.Diagnostics.Logs;
using ModificationsKnown.Infrastructure.Diagnostics.Logs;

namespace ModificationsKnown.App.Services;

/// <summary>Thin seam over the platform clipboard so the view model stays testable.</summary>
public interface IClipboardService
{
    Task<bool> SetTextAsync(string text);
}

public sealed class ClipboardService : IClipboardService
{
    private readonly Func<Window?> _ownerProvider;

    public ClipboardService(Func<Window?> ownerProvider) => _ownerProvider = ownerProvider;

    public async Task<bool> SetTextAsync(string text)
    {
        var clipboard = _ownerProvider()?.Clipboard;
        if (clipboard is null) return false;

        await clipboard.SetTextAsync(text);
        return true;
    }
}

/// <summary>Opens an existing file or folder with the shell. Never creates
/// anything: a missing target is reported and nothing is launched.</summary>
public interface IExternalLauncher
{
    bool TryOpenFile(string path, out string? error);

    bool TryOpenFolder(string path, out string? error);
}

public sealed class ShellExternalLauncher : IExternalLauncher
{
    public bool TryOpenFile(string path, out string? error)
    {
        error = null;

        if (!File.Exists(path))
        {
            error = "目标文件不存在，未打开。";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public bool TryOpenFolder(string path, out string? error)
    {
        error = null;

        if (!Directory.Exists(path))
        {
            error = "目标文件夹不存在，未打开。";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}

/// <summary>Describes the log sources the page can offer. Kept behind an
/// interface so view-model tests never touch the real game folders.</summary>
public interface ILogSourceProvider
{
    /// <summary>Raised when the selected game instance changed, so the page can
    /// re-read a BepInEx source without polling.</summary>
    event EventHandler? SourcesChanged;

    IReadOnlyList<LogSource> Describe();
}

public sealed class GameLogSourceProvider : ILogSourceProvider, IDisposable
{
    private readonly GameContext _game;

    public GameLogSourceProvider(GameContext game)
    {
        _game = game;
        _game.Changed += OnGameChanged;
    }

    public event EventHandler? SourcesChanged;

    public IReadOnlyList<LogSource> Describe()
    {
        // BepInEx is always offered: without an instance it stays selected but
        // has no path, so the page can show "no instance" instead of silently
        // jumping to a Unity log.
        var sources = new List<LogSource>();
        var installation = _game.Selected;

        sources.Add(installation is null
            ? new LogSource(SourceKind.BepInEx, "bepinex:none")
            : LogSourceLocations.BepInEx(installation.GameDirectory, installation.GameDirectory));

        sources.Add(LogSourceLocations.UnityCurrent());
        sources.Add(LogSourceLocations.UnityPrevious());

        return sources;
    }

    public void Dispose() => _game.Changed -= OnGameChanged;

    private void OnGameChanged(object? sender, EventArgs e) =>
        SourcesChanged?.Invoke(this, EventArgs.Empty);
}
