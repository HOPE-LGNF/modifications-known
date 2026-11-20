using ModificationsKnown.Core.Diagnostics.Logs;

namespace ModificationsKnown.App.ViewModels;

/// <summary>Display wrapper: keeps presentation (badge text, colours) out of Core.</summary>
public sealed class LogEntryRow
{
    public LogEntryRow(LogEntry entry) => Entry = entry;

    public LogEntry Entry { get; }

    public string LevelText => Entry.Level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Fatal => "FATAL",
        _ => "UNKNOWN"
    };

    public bool IsInfoLevel => Entry.Level is LogLevel.Debug or LogLevel.Info;

    public bool IsWarningLevel => Entry.Level == LogLevel.Warning;

    public bool IsErrorLevel => Entry.Level is LogLevel.Error or LogLevel.Fatal;

    public string Source => Entry.Source;

    public string Message => Entry.Message;

    public string FirstLine
    {
        get
        {
            var newline = Entry.Message.IndexOfAny(['\r', '\n']);
            return newline < 0 ? Entry.Message : Entry.Message[..newline];
        }
    }

    public string Raw => Entry.Raw;
}
