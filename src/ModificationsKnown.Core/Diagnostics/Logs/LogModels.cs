using System.Text.RegularExpressions;

namespace ModificationsKnown.Core.Diagnostics.Logs;

public enum LogLevel
{
    Unknown = 0,
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}

public enum SourceKind
{
    BepInEx,
    UnityCurrent,
    UnityPrevious,
    ManualFile
}

public enum LogReadOutcome
{
    Ok,
    Missing,
    Empty,
    Failed,
    Cancelled
}

public enum LogLevelFilter
{
    All,
    WarningsAndErrors,
    Errors
}

/// <summary>Which fields a search matches. Kept explicit so the UI can say
/// what it is searching instead of silently searching everything.</summary>
public enum LogSearchScope
{
    All,
    Source,
    Message
}

/// <summary>Identity of what is being read. <see cref="Identity"/> is the value
/// a snapshot is validated against; it must change whenever the underlying file
/// could be a different one (kind, instance or path).</summary>
public sealed record LogSource(
    SourceKind Kind,
    string Identity,
    string? Path = null,
    string? InstanceKey = null);

public sealed record LogEntry(
    LogLevel Level,
    string LevelText,
    string Source,
    string Message,
    string Raw,
    long StartOffset);

public sealed record LogReadResult(
    LogReadOutcome Outcome,
    IReadOnlyList<LogEntry> Entries,
    bool Truncated,
    string? Error)
{
    public static LogReadResult Ok(IReadOnlyList<LogEntry> entries, bool truncated) =>
        new(LogReadOutcome.Ok, entries, truncated, null);

    public static LogReadResult Missing() =>
        new(LogReadOutcome.Missing, Array.Empty<LogEntry>(), false, null);

    public static LogReadResult Empty() =>
        new(LogReadOutcome.Empty, Array.Empty<LogEntry>(), false, null);

    public static LogReadResult Failed(string error) =>
        new(LogReadOutcome.Failed, Array.Empty<LogEntry>(), false, error);

    public static LogReadResult Cancelled() =>
        new(LogReadOutcome.Cancelled, Array.Empty<LogEntry>(), false, null);
}

public sealed record LogSnapshot(
    LogSource Source,
    IReadOnlyList<LogEntry> Entries,
    LogReadOutcome Outcome,
    bool Truncated,
    DateTimeOffset ReadAt,
    string? Error);

public sealed record LogLoadResult(
    bool Adopted,
    LogReadOutcome Outcome,
    string? Error,
    bool KeptPreviousSnapshot);

/// <summary>Reads one log source. Implementations open the file once per call
/// and return a bounded snapshot; they never write to the log.</summary>
public interface ILogReader
{
    Task<LogReadResult> ReadAsync(LogSource source, CancellationToken cancellationToken = default);
}

/// <summary>Turns raw log text into entries. Pure: no IO, no UI text.</summary>
public interface ILogTextParser
{
    IReadOnlyList<LogEntry> Parse(string text, bool headTruncated);
}

public static class LogParsers
{
    public static ILogTextParser For(SourceKind kind) =>
        kind == SourceKind.BepInEx ? new BepInExLogParser() : new UnityLogParser();
}

/// <summary>Pure filtering over an existing snapshot: no IO.</summary>
public static class LogFilter
{
    public static IReadOnlyList<LogEntry> Apply(
        IReadOnlyList<LogEntry> entries,
        LogLevelFilter level,
        LogSearchScope scope,
        string? search)
    {
        var needle = search?.Trim() ?? string.Empty;

        return entries
            .Where(entry => MatchesLevel(entry.Level, level))
            .Where(entry => needle.Length == 0 || MatchesSearch(entry, scope, needle))
            .ToList();
    }

    private static bool MatchesLevel(LogLevel level, LogLevelFilter filter) => filter switch
    {
        LogLevelFilter.All => true,
        LogLevelFilter.WarningsAndErrors => level is LogLevel.Warning or LogLevel.Error or LogLevel.Fatal,
        LogLevelFilter.Errors => level is LogLevel.Error or LogLevel.Fatal,
        _ => false
    };

    private static bool MatchesSearch(LogEntry entry, LogSearchScope scope, string needle) => scope switch
    {
        LogSearchScope.Source => entry.Source.Contains(needle, StringComparison.OrdinalIgnoreCase),
        LogSearchScope.Message => entry.Message.Contains(needle, StringComparison.OrdinalIgnoreCase),
        _ => entry.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
             || entry.Source.Contains(needle, StringComparison.OrdinalIgnoreCase)
    };
}

/// <summary>BepInEx 5 LogOutput.log: "[Level: Source] message", with indented
/// continuation lines (stack frames) belonging to the previous entry.</summary>
internal sealed partial class BepInExLogParser : ILogTextParser
{
    [GeneratedRegex(@"^\[(?<level>[^:\]]+?)\s*:\s*(?<source>[^\]]*)\]\s?(?<message>.*)$")]
    private static partial Regex PrefixPattern();

    public IReadOnlyList<LogEntry> Parse(string text, bool headTruncated)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var entries = new List<LogEntry>();
        var buffer = new List<string>();
        var offset = 0L;
        long entryOffset = 0;
        LogLevel level = LogLevel.Unknown;
        var levelText = "Unknown";
        var source = string.Empty;

        void Flush()
        {
            if (buffer.Count == 0) return;

            var raw = string.Join('\n', buffer);
            var firstLine = buffer[0];
            var message = firstLine;
            var match = PrefixPattern().Match(firstLine);
            if (match.Success)
            {
                message = match.Groups["message"].Value;
                if (buffer.Count > 1)
                {
                    message += "\n" + string.Join('\n', buffer.Skip(1));
                }
            }

            entries.Add(new LogEntry(level, levelText, source, message, raw, entryOffset));
            buffer.Clear();
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var match = PrefixPattern().Match(line);

            if (match.Success)
            {
                Flush();
                entryOffset = offset;
                var rawLevel = match.Groups["level"].Value.Trim();
                levelText = rawLevel;
                // A truncated head means the true prefix may have been cut off,
                // so a line that merely looks valid is not trusted.
                level = headTruncated && entries.Count == 0 && buffer.Count == 0
                    ? LogLevel.Unknown
                    : MapLevel(rawLevel);
                source = match.Groups["source"].Value.Trim();
                buffer.Add(line);
            }
            else if (buffer.Count > 0)
            {
                // Continuation of the previous entry: stack frames, wrapped text.
                buffer.Add(line);
            }
            else if (line.Length > 0)
            {
                entryOffset = offset;
                level = LogLevel.Unknown;
                levelText = "Unknown";
                source = string.Empty;
                buffer.Add(line);
            }

            offset += line.Length + 1;
        }

        Flush();
        return entries;
    }

    private static LogLevel MapLevel(string text) => text.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Info,
        "message" => LogLevel.Info,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "fatal" or "critical" => LogLevel.Fatal,
        _ => LogLevel.Unknown
    };
}

/// <summary>Unity Player.log: no level prefixes. Indented lines continue the
/// previous entry so a stack trace stays attached to its exception.</summary>
internal sealed class UnityLogParser : ILogTextParser
{
    public IReadOnlyList<LogEntry> Parse(string text, bool headTruncated)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var entries = new List<LogEntry>();
        var buffer = new List<string>();
        var offset = 0L;
        long entryOffset = 0;

        void Flush()
        {
            if (buffer.Count == 0) return;

            var raw = string.Join('\n', buffer);
            entries.Add(new LogEntry(LogLevel.Unknown, "Unknown", string.Empty, raw, raw, entryOffset));
            buffer.Clear();
        }

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                // Blank lines are separators, not entries; keep them inside a stack.
                if (buffer.Count > 0) buffer.Add(line);
            }
            else if ((line[0] == ' ' || line[0] == '\t') && buffer.Count > 0)
            {
                buffer.Add(line);
            }
            else
            {
                Flush();
                entryOffset = offset;
                buffer.Add(line);
            }

            offset += line.Length + 1;
        }

        Flush();
        return entries;
    }
}
