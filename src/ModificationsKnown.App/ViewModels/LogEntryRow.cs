using Avalonia.Media;
using ModificationsKnown.Core.Diagnostics.Logs;

namespace ModificationsKnown.App.ViewModels;

/// <summary>Display wrapper: keeps presentation (badge text, colours) out of Core.</summary>
public sealed class LogEntryRow
{
    private static readonly IBrush UnknownBrush = new SolidColorBrush(Color.Parse("#8C9A93"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#9FC2B2"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#E5B567"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E07A7A"));

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

    public IBrush LevelBrush => Entry.Level switch
    {
        LogLevel.Debug or LogLevel.Info => InfoBrush,
        LogLevel.Warning => WarningBrush,
        LogLevel.Error or LogLevel.Fatal => ErrorBrush,
        _ => UnknownBrush
    };

    public string Source => Entry.Source;

    public string Message => Entry.Message;

    public string FirstLine
    {
        get
        {
            var newline = Entry.Message.IndexOf('\n');
            return newline < 0 ? Entry.Message : Entry.Message[..newline];
        }
    }

    public string Raw => Entry.Raw;
}
