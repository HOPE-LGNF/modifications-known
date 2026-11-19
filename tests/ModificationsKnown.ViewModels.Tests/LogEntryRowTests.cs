using ModificationsKnown.App.ViewModels;
using ModificationsKnown.Core.Diagnostics.Logs;

namespace ModificationsKnown.ViewModels.Tests;

/// <summary>The list summary must stop at the entry's own line break: after the
/// parser stopped normalising CRLF, a bare '\r' would leak into the row.</summary>
public sealed class LogEntryRowTests
{
    private static LogEntryRow Row(string message) =>
        new(new LogEntry(LogLevel.Info, "Info", "BepInEx", message, message, 0));

    [Fact]
    public void FirstLineStopsBeforeCrLf()
    {
        Assert.Equal("first", Row("first\r\nstack").FirstLine);
    }

    [Fact]
    public void FirstLineStopsBeforeLoneCr()
    {
        Assert.Equal("first", Row("first\rstack").FirstLine);
    }
}
