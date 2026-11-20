using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.App.Tests;

/// <summary>
/// Test doubles for the external boundaries a page view model touches. They
/// record what the view model asked for instead of hitting settings files,
/// process lists or pickers.
/// </summary>
internal sealed class FakeAppShell : IAppShell
{
    public FakeAppShell(IAppSettingsService settings) => Settings = settings;

    public GameContext Game { get; } = new(new GameInstanceStore(
        Path.Combine(Path.GetTempPath(), "mk-app-tests-" + Guid.NewGuid().ToString("N"), "instances.json")));

    public IAppSettingsService Settings { get; }

    public string? LastStatus { get; private set; }

    public StatusKind LastStatusKind { get; private set; }

    public void SetStatus(string message, StatusKind kind = StatusKind.Info)
    {
        LastStatus = message;
        LastStatusKind = kind;
    }

    public Task RefreshGameAsync() => Task.CompletedTask;

    public bool CanLaunch => false;

    public string LaunchButtonText => "启动";

    public bool IsCloseAction => false;

    public Task LaunchGameAsync() => Task.CompletedTask;

    public event Action<IReadOnlyCollection<string>>? ModsChanged
    {
        add { }
        remove { }
    }

    public void NotifyModsChanged(IReadOnlyCollection<string> packageNames)
    {
    }

    public void NavigateTo(string key)
    {
    }

    public void NavigateSettings(string focus)
    {
    }
}

internal sealed class FakeAppSettingsService : IAppSettingsService
{
    public AppSettings Current { get; } = new();

    public int SaveCount { get; private set; }

    public event EventHandler? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

internal sealed class FakeFilePickerService : IFilePickerService
{
    public Task<string?> PickExecutableAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickArchiveAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickThemeFileAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickLogFileAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickDataFolderAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickBackupFolderAsync() => Task.FromResult<string?>(null);
}

internal sealed class FakeAppLogger : IAppLogger
{
    public string LogDirectory => string.Empty;

    public void Debug(string message)
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }

    public Task<string> ReadRecentAsync(int maxCharacters = 60_000, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);
}
