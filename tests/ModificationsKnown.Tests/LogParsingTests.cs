using ModificationsKnown.Core.Diagnostics.Logs;

namespace ModificationsKnown.Tests;

public sealed class LogParsingTests
{
    private const string BepInExSample = """
        [Message:   BepInEx] BepInEx 5.4.23.3 - Casualties Unknown
        [Info   :   BepInEx] Loading [QoL 1.0.0]
        [Warning:   HarmonyX] Patching failed for Foo
        [Error  : Unity Log] NullReferenceException: Object reference not set
          at Foo.Bar () [0x00000]
        [Info   :   BepInEx] Chainloader startup complete
        """;

    [Fact]
    public void BepInExEntryKeepsLfAndIndentation()
    {
        const string text = "[Info   : BepInEx] boom\n  at A\n\tat B";

        var entries = LogParsers.For(SourceKind.BepInEx).Parse(text, headTruncated: false);

        var entry = Assert.Single(entries);
        Assert.Equal(text, entry.Raw);
        Assert.Equal("boom\n  at A\n\tat B", entry.Message);
    }

    [Fact]
    public void BepInExEntryKeepsCrLfAndIndentation()
    {
        const string text = "[Info   : BepInEx] boom\r\n  at A\r\n\tat B";

        var entries = LogParsers.For(SourceKind.BepInEx).Parse(text, headTruncated: false);

        var entry = Assert.Single(entries);
        Assert.Equal(text, entry.Raw);
        Assert.Equal("boom\r\n  at A\r\n\tat B", entry.Message);
    }

    [Fact]
    public void BepInExEntryKeepsLoneCrAndIndentation()
    {
        const string text = "[Error  : Unity Log] boom\r  at A\r\tat B";

        var entries = LogParsers.For(SourceKind.BepInEx).Parse(text, headTruncated: false);

        var entry = Assert.Single(entries);
        Assert.Equal(text, entry.Raw);
        Assert.Equal("boom\r  at A\r\tat B", entry.Message);
    }

    [Fact]
    public void UnityEntryKeepsLoneCrAndIndentation()
    {
        const string text = "Line one\r  at X\r\tat Y";

        var entries = LogParsers.For(SourceKind.UnityCurrent).Parse(text, headTruncated: false);

        var entry = Assert.Single(entries);
        Assert.Equal(text, entry.Raw);
        Assert.Equal(text, entry.Message);
    }

    [Fact]
    public void UnityEntryKeepsCrLfAndIndentation()
    {
        const string text = "Line one\r\n  at X\r\n  at Y";

        var entries = LogParsers.For(SourceKind.UnityCurrent).Parse(text, headTruncated: false);

        var entry = Assert.Single(entries);
        Assert.Equal(text, entry.Raw);
        Assert.Equal(text, entry.Message);
    }

    [Fact]
    public void BepInExParsesLevelSourceAndMessage()
    {
        var entries = LogParsers.For(SourceKind.BepInEx).Parse(BepInExSample, headTruncated: false);

        Assert.Equal(5, entries.Count);

        var banner = entries[0];
        Assert.Equal(LogLevel.Info, banner.Level);
        Assert.Equal("Message", banner.LevelText);
        Assert.Equal("BepInEx", banner.Source);

        var info = entries[1];
        Assert.Equal(LogLevel.Info, info.Level);
        Assert.Equal("BepInEx", info.Source);
        Assert.Equal("Loading [QoL 1.0.0]", info.Message);

        Assert.Equal(LogLevel.Warning, entries[2].Level);
        Assert.Equal("HarmonyX", entries[2].Source);

        var error = entries[3];
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal("Unity Log", error.Source);
        Assert.Contains("NullReferenceException", error.Message);
    }

    [Fact]
    public void BepInExAttachesContinuationLinesToThePreviousEntry()
    {
        var entries = LogParsers.For(SourceKind.BepInEx).Parse(BepInExSample, headTruncated: false);

        var error = entries[3];
        Assert.Contains("at Foo.Bar", error.Message);
        Assert.Contains("at Foo.Bar", error.Raw);
        Assert.DoesNotContain(entries, entry => entry.Message.Contains("at Foo.Bar")
                                               && entry.Level != LogLevel.Error);
    }

    [Fact]
    public void BepInExLevelWordsAreCaseInsensitive()
    {
        var entries = LogParsers.For(SourceKind.BepInEx)
            .Parse("[error  : Unity Log] boom", headTruncated: false);

        Assert.Single(entries);
        Assert.Equal(LogLevel.Error, entries[0].Level);
    }

    [Fact]
    public void BepInExLegalLevelsAreNotTreatedAsUnknown()
    {
        var entries = LogParsers.For(SourceKind.BepInEx).Parse(
            "[Message: BepInEx] banner\n[Fatal  : BepInEx] dead",
            headTruncated: false);

        Assert.Equal(LogLevel.Info, entries[0].Level);
        Assert.Equal("Message", entries[0].LevelText);
        Assert.Equal(LogLevel.Fatal, entries[1].Level);
        Assert.DoesNotContain(entries, entry => entry.Level == LogLevel.Unknown);
    }

    [Fact]
    public void BepInExUnprefixedLeadingLinesBecomeUnknown()
    {
        var entries = LogParsers.For(SourceKind.BepInEx)
            .Parse("garbage before the first entry\n[Info   : BepInEx] ok", headTruncated: false);

        Assert.Equal(2, entries.Count);
        Assert.Equal(LogLevel.Unknown, entries[0].Level);
        Assert.Contains("garbage", entries[0].Message);
    }

    [Fact]
    public void TruncatedHeadMarksTheFirstEntryUnknown()
    {
        var entries = LogParsers.For(SourceKind.BepInEx)
            .Parse("BepInEx] Loading [QoL\n[Info   : BepInEx] ok", headTruncated: true);

        Assert.Equal(2, entries.Count);
        Assert.Equal(LogLevel.Unknown, entries[0].Level);
    }

    [Fact]
    public void TruncatedHeadDowngradesAnOtherwiseValidFirstEntry()
    {
        var entries = LogParsers.For(SourceKind.BepInEx)
            .Parse("[Info   : BepInEx] cut", headTruncated: true);

        Assert.Single(entries);
        Assert.Equal(LogLevel.Unknown, entries[0].Level);
    }

    [Fact]
    public void UnityLinesWithoutAPrefixStayUnknown()
    {
        const string sample = """
            Initialize engine version: 2022.3.10f1
            NullReferenceException: Object reference not set
              at Foo.Bar () (at Assets/Foo.cs:12)
            System.ArgumentException: bad argument
            """;

        var entries = LogParsers.For(SourceKind.UnityCurrent).Parse(sample, headTruncated: false);

        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.Equal(LogLevel.Unknown, entry.Level));
        Assert.Equal("Initialize engine version: 2022.3.10f1", entries[0].Message);
        Assert.Contains("at Foo.Bar", entries[1].Message);
        Assert.Contains("ArgumentException", entries[2].Message);
    }

    [Fact]
    public void BlankLinesDoNotCreateEntries()
    {
        var entries = LogParsers.For(SourceKind.UnityCurrent)
            .Parse("first\n\n\nsecond\n", headTruncated: false);

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void EntriesKeepMonotonicStartOffsets()
    {
        var entries = LogParsers.For(SourceKind.BepInEx).Parse(BepInExSample, headTruncated: false);

        for (var index = 1; index < entries.Count; index++)
        {
            Assert.True(entries[index].StartOffset > entries[index - 1].StartOffset);
        }
    }
}

public sealed class LogFilterTests
{
    private static LogEntry Entry(LogLevel level, string message, string source = "BepInEx") =>
        new(level, level.ToString(), source, message, message, 0);

    private static readonly IReadOnlyList<LogEntry> Sample =
    [
        Entry(LogLevel.Info, "loading mods"),
        Entry(LogLevel.Warning, "patching failed"),
        Entry(LogLevel.Error, "NullReferenceException in Foo", "Unity Log"),
        Entry(LogLevel.Unknown, "raw unity line", ""),
        Entry(LogLevel.Fatal, "cannot continue")
    ];

    [Fact]
    public void AllIncludesUnknown()
    {
        Assert.Equal(5, LogFilter.Apply(Sample, LogLevelFilter.All, LogSearchScope.All, null).Count);
    }

    [Fact]
    public void WarningsAndErrorsExcludesInfoAndUnknown()
    {
        var result = LogFilter.Apply(Sample, LogLevelFilter.WarningsAndErrors, LogSearchScope.All, null);

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, entry => entry.Level is LogLevel.Info or LogLevel.Unknown);
    }

    [Fact]
    public void ErrorsOnlyKeepsErrorAndFatal()
    {
        var result = LogFilter.Apply(Sample, LogLevelFilter.Errors, LogSearchScope.All, null);

        Assert.Equal(2, result.Count);
        Assert.All(result, entry => Assert.True(entry.Level is LogLevel.Error or LogLevel.Fatal));
    }

    [Fact]
    public void SearchIsCaseInsensitiveAndMatchesMessageAndSource()
    {
        Assert.Single(LogFilter.Apply(Sample, LogLevelFilter.All, LogSearchScope.All, "NULlREFERENCE"));
        Assert.Single(LogFilter.Apply(Sample, LogLevelFilter.All, LogSearchScope.All, "unity log"));
    }

    [Fact]
    public void SearchIsLiteralNotRegex()
    {
        Assert.Empty(LogFilter.Apply(Sample, LogLevelFilter.All, LogSearchScope.All, ".*"));
        Assert.Empty(LogFilter.Apply(Sample, LogLevelFilter.All, LogSearchScope.All, "(?i)mods"));
    }

    [Fact]
    public void EmptySearchKeepsEverything()
    {
        Assert.Equal(5, LogFilter.Apply(Sample, LogLevelFilter.All, LogSearchScope.All, "  ").Count);
    }

    [Fact]
    public void SourceScopeIgnoresTheMessageBody()
    {
        Assert.Single(LogFilter.Apply(Sample, LogLevelFilter.All, LogSearchScope.Source, "unity log"));
        Assert.Empty(LogFilter.Apply(Sample, LogLevelFilter.All, LogSearchScope.Source, "NullReferenceException"));
    }

    [Fact]
    public void MessageScopeIgnoresTheSource()
    {
        Assert.Single(LogFilter.Apply(Sample, LogLevelFilter.All, LogSearchScope.Message, "NullReferenceException"));
        Assert.Empty(LogFilter.Apply(Sample, LogLevelFilter.All, LogSearchScope.Message, "unity log"));
    }
}
