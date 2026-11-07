namespace ModificationsKnown.Core.Diagnostics;

public interface IAppLogger
{
    string LogDirectory { get; }

    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);

    Task<string> ReadRecentAsync(int maxCharacters = 60_000, CancellationToken cancellationToken = default);
}
