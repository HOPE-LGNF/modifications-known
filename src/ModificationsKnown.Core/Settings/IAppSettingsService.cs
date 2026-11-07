namespace ModificationsKnown.Core.Settings;

public interface IAppSettingsService
{
    AppSettings Current { get; }

    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
