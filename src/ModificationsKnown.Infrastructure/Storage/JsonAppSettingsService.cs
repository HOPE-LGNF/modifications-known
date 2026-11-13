using System.Text.Json;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Storage;

public sealed class JsonAppSettingsService : IAppSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();

        if (!File.Exists(AppPaths.SettingsFile))
        {
            Current = new AppSettings();
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(AppPaths.SettingsFile, cancellationToken).ConfigureAwait(false);
            Current = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(Current, SerializerOptions);
            var temporaryFile = AppPaths.SettingsFile + ".tmp";

            await File.WriteAllTextAsync(temporaryFile, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryFile, AppPaths.SettingsFile, overwrite: true);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
