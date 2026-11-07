using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.App.Services;

public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error
}

public interface IAppShell
{
    GameContext Game { get; }

    IAppSettingsService Settings { get; }

    void SetStatus(string message, StatusKind kind = StatusKind.Info);

    Task RefreshGameAsync();

    void NavigateTo(string key);
}

public interface IAsyncPage
{
    Task OnActivatedAsync();
}
