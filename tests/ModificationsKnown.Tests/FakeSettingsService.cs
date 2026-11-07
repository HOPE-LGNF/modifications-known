using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.Tests;

internal sealed class FakeSettingsService : IAppSettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
