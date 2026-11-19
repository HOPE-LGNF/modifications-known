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

/// <summary>Why a snapshot is incomplete. The UI must describe the actual
/// reason instead of always claiming the 2 MiB tail window.</summary>
[Flags]
public enum LogTruncation
{
    None = 0,

    /// <summary>The tail window cut older content.</summary>
    Head = 1,

    /// <summary>Older entries were dropped by the entry-count budget.</summary>
    EntryCount = 2,

    /// <summary>One or more entries were shortened to the per-entry budget.</summary>
    EntryLength = 4
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
    LogTruncation Truncation,
    bool HadDecodeFallback,
    long BytesRead,
    string? Error)
{
    public bool Truncated => Truncation != LogTruncation.None;

    public static LogReadResult Ok(
        IReadOnlyList<LogEntry> entries,
        LogTruncation truncation = LogTruncation.None,
        bool hadDecodeFallback = false,
        long bytesRead = 0) =>
        new(LogReadOutcome.Ok, entries, truncation, hadDecodeFallback, bytesRead, null);

    public static LogReadResult Missing(long bytesRead = 0) =>
        new(LogReadOutcome.Missing, Array.Empty<LogEntry>(), LogTruncation.None, false, bytesRead, null);

    public static LogReadResult Empty(long bytesRead = 0) =>
        new(LogReadOutcome.Empty, Array.Empty<LogEntry>(), LogTruncation.None, false, bytesRead, null);

    public static LogReadResult Failed(string error, long bytesRead = 0) =>
        new(LogReadOutcome.Failed, Array.Empty<LogEntry>(), LogTruncation.None, false, bytesRead, error);

    public static LogReadResult Cancelled(long bytesRead = 0) =>
        new(LogReadOutcome.Cancelled, Array.Empty<LogEntry>(), LogTruncation.None, false, bytesRead, null);
}

public sealed record LogSnapshot(
    LogSource Source,
    IReadOnlyList<LogEntry> Entries,
    LogReadOutcome Outcome,
    LogTruncation Truncation,
    bool HadDecodeFallback,
    long BytesRead,
    DateTimeOffset ReadAt,
    string? Error)
{
    public bool Truncated => Truncation != LogTruncation.None;
}

public enum LogLoadError
{
    None,
    SourceNotSelected
}

public sealed record LogLoadResult(
    bool Adopted,
    LogReadOutcome Outcome,
    string? Error,
    bool KeptPreviousSnapshot,
    LogLoadError ErrorKind = LogLoadError.None);

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

/// <summary>Splits text into lines while keeping each line's offset in the
/// original string. Parsers must report offsets in the same coordinate system
/// the reader maps to file bytes, so CRLF must not be normalized away before
/// offsets are computed.</summary>
internal static class LogLines
{
    public static IEnumerable<(string Text, long Offset)> Enumerate(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current is not ('\r' or '\n'))
            {
                continue;
            }

            yield return (text[start..index], start);

            if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        var endedWithBreak = text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r');
        if (start < text.Length || (start == text.Length && endedWithBreak))
        {
            yield return (text[start..], start);
        }
    }
}

/// <summary>Helpers that keep the original text coordinates intact. Entry
/// separators are dropped, but line breaks inside an entry are preserved.</summary>
internal static class LogText
{
    public static int IndexOfBreak(string value) => value.IndexOfAny(['\r', '\n']);

    public static string TrimTrailingBreak(string value)
    {
        if (value.EndsWith("\r\n", StringComparison.Ordinal)) return value[..^2];
        return value.Length > 0 && (value[^1] == '\r' || value[^1] == '\n')
            ? value[..^1]
            : value;
    }
}

/// <summary>BepInEx 5 LogOutput.log: "[Level: Source] message", with indented
/// continuation lines (stack frames) belonging to the previous entry.</summary>
internal sealed partial class BepInExLogParser : ILogTextParser
{
    [GeneratedRegex(@"^\[(?<level>[^:\]]+?)\s*:\s*(?<source>[^\]]*)\]\s?(?<message>.*)$")]
    private static partial Regex PrefixPattern();

    public IReadOnlyList<LogEntry> Parse(string text, bool headTruncated)
    {
        var entries = new List<LogEntry>();
        var buffer = new List<string>();
        var lineStart = 0;
        long entryOffset = 0;
        LogLevel level = LogLevel.Unknown;
        var levelText = "Unknown";
        var source = string.Empty;

        void Flush(int endExclusive)
        {
            if (buffer.Count == 0) return;

            // Slice the original text so entry-internal line breaks and
            // indentation survive exactly as written (LF, CRLF and CR alike).
            var raw = LogText.TrimTrailingBreak(text[lineStart..endExclusive]);
            var firstBreak = LogText.IndexOfBreak(raw);
            var firstLine = firstBreak < 0 ? raw : raw[..firstBreak];
            var message = raw;
            var match = PrefixPattern().Match(firstLine);
            if (match.Success)
            {
                message = match.Groups["message"].Value;
                if (firstBreak >= 0)
                {
                    message += raw[firstBreak..];
                }
            }

            entries.Add(new LogEntry(level, levelText, source, message, raw, entryOffset));
            buffer.Clear();
        }

        foreach (var (line, offset) in LogLines.Enumerate(text))
        {
            var match = PrefixPattern().Match(line);

            if (match.Success)
            {
                Flush((int)offset);
                lineStart = (int)offset;
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
                lineStart = (int)offset;
                entryOffset = offset;
                level = LogLevel.Unknown;
                levelText = "Unknown";
                source = string.Empty;
                buffer.Add(line);
            }
        }

        Flush(text.Length);
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
        var entries = new List<LogEntry>();
        var buffer = new List<string>();
        var lineStart = 0;
        long entryOffset = 0;

        void Flush(int endExclusive)
        {
            if (buffer.Count == 0) return;

            var raw = LogText.TrimTrailingBreak(text[lineStart..endExclusive]);
            entries.Add(new LogEntry(LogLevel.Unknown, "Unknown", string.Empty, raw, raw, entryOffset));
            buffer.Clear();
        }

        foreach (var (line, offset) in LogLines.Enumerate(text))
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
                Flush((int)offset);
                entryOffset = offset;
                lineStart = (int)offset;
                buffer.Add(line);
            }
        }

        Flush(text.Length);
        return entries;
    }
}
