using System.Text;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Infrastructure.Diagnostics;

public sealed class FileAppLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly string _sessionFile;

    public FileAppLogger()
    {
        AppPaths.EnsureCreated();
        _sessionFile = Path.Combine(AppPaths.LogsDirectory, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public string LogDirectory => AppPaths.LogsDirectory;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    public async Task<string> ReadRecentAsync(int maxCharacters = 60_000, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_sessionFile)) return string.Empty;

        await using var stream = new FileStream(
            _sessionFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return text.Length <= maxCharacters ? text : text[^maxCharacters..];
    }

    private void Write(string level, string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

        lock (_gate)
        {
            try
            {
                File.AppendAllText(_sessionFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take down the launcher.
            }
        }

        System.Diagnostics.Debug.WriteLine(line);
    }
}
