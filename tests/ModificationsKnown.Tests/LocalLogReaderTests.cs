using System.Text;
using ModificationsKnown.Core.Diagnostics.Logs;
using ModificationsKnown.Infrastructure.Diagnostics.Logs;

namespace ModificationsKnown.Tests;

public sealed class LocalLogReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "mk-log-tests", Guid.NewGuid().ToString("n"));

    public LocalLogReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private LogSource Source(string path) =>
        new(SourceKind.BepInEx, $"bepinex:{path}", path, "test");

    private static string BepInExLines(int count, string prefix = "line") =>
        string.Join('\n', Enumerable.Range(0, count).Select(i => $"[Info   : BepInEx] {prefix} {i}"));

    [Fact]
    public async Task MissingFileReturnsMissing()
    {
        var reader = new LocalLogReader();
        var result = await reader.ReadAsync(Source(Path.Combine(_directory, "absent.log")));

        Assert.Equal(LogReadOutcome.Missing, result.Outcome);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task EmptyFileReturnsEmpty()
    {
        var path = Write("empty.log", string.Empty);
        var result = await new LocalLogReader().ReadAsync(Source(path));

        Assert.Equal(LogReadOutcome.Empty, result.Outcome);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task ReadsOnlyTheTailWhenTheFileExceedsTheByteBudget()
    {
        var path = Write("big.log", BepInExLines(400));
        var reader = new LocalLogReader(new LocalLogReaderOptions(MaxBytes: 2048, MaxEntries: 5000));

        var result = await reader.ReadAsync(Source(path));

        Assert.Equal(LogReadOutcome.Ok, result.Outcome);
        Assert.True(result.Truncated);
        Assert.NotEmpty(result.Entries);
        Assert.Contains("line 399", result.Entries[^1].Message);
        Assert.DoesNotContain(result.Entries, entry => entry.Message.Contains("line 0"));
    }

    [Fact]
    public async Task KeepsOnlyTheNewestEntriesWhenTheCountExceedsTheLimit()
    {
        var path = Write("many.log", BepInExLines(10));
        var reader = new LocalLogReader(new LocalLogReaderOptions(MaxEntries: 3));

        var result = await reader.ReadAsync(Source(path));

        Assert.Equal(3, result.Entries.Count);
        Assert.Contains("line 7", result.Entries[0].Message);
        Assert.Contains("line 9", result.Entries[^1].Message);
    }

    [Fact]
    public async Task Utf8BomIsStrippedFromTheFirstEntry()
    {
        var path = Write("bom.log", BepInExLines(2), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var result = await new LocalLogReader().ReadAsync(Source(path));

        Assert.Equal(LogReadOutcome.Ok, result.Outcome);
        Assert.StartsWith("[Info", result.Entries[0].Raw);
        Assert.DoesNotContain('\uFEFF', result.Entries[0].Raw);
    }

    [Fact]
    public async Task Utf16LittleEndianBomIsDecoded()
    {
        var path = Write("utf16.log", BepInExLines(2), Encoding.Unicode);
        var result = await new LocalLogReader().ReadAsync(Source(path));

        Assert.Equal(LogReadOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Entries.Count);
        Assert.Contains("line 1", result.Entries[^1].Message);
        Assert.All(result.Entries, entry => Assert.DoesNotContain('\0', entry.Message));
    }

    [Fact]
    public async Task InvalidUtf8BytesAreReplacedInsteadOfThrowing()
    {
        var path = Path.Combine(_directory, "broken.log");
        var bytes = Encoding.ASCII.GetBytes("[Info   : BepInEx] ok\n[Error  : Unity Log] bad ")
            .Concat(new byte[] { 0xC3, 0x28 })
            .Concat(Encoding.ASCII.GetBytes(" bytes"))
            .ToArray();
        File.WriteAllBytes(path, bytes);

        var result = await new LocalLogReader().ReadAsync(Source(path));

        Assert.Equal(LogReadOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(LogLevel.Error, result.Entries[1].Level);
    }

    [Fact]
    public async Task AnOversizedEntryIsTruncatedAndMarked()
    {
        var path = Write("long.log", $"[Info   : BepInEx] {new string('x', 500)}");
        var reader = new LocalLogReader(new LocalLogReaderOptions(MaxEntryChars: 50));

        var result = await reader.ReadAsync(Source(path));

        Assert.True(result.Truncated);
        Assert.True(result.Entries[0].Message.Length <= 51);
        Assert.True(result.Entries[0].Raw.Length <= 51);
    }

    [Fact]
    public async Task AlreadyCancelledTokenReturnsCancelled()
    {
        var path = Write("cancel.log", BepInExLines(2));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new LocalLogReader().ReadAsync(Source(path), cts.Token);

        Assert.Equal(LogReadOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task CancellingAfterTheProbeReturnsCancelledAndReleasesTheHandle()
    {
        var path = Write("cancel-mid.log", BepInExLines(50));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new LocalLogReader
        {
            AfterProbeHook = () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        using var cts = new CancellationTokenSource();

        var task = Task.Run(() => reader.ReadAsync(Source(path), cts.Token));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "reader never reached the probe seam");

        cts.Cancel();
        release.Set();

        var result = await task;

        Assert.Equal(LogReadOutcome.Cancelled, result.Outcome);
        // FileShare.None only succeeds when the reader closed its handle.
        using var exclusive = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task AReplacedFileDoesNotLeakIntoTheSameRead()
    {
        var path = Write("rotate.log", BepInExLines(3, "old"));
        var rotated = path + ".rotated";
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new LocalLogReader
        {
            AfterProbeHook = () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var task = Task.Run(() => reader.ReadAsync(Source(path)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "reader never reached the probe seam");

        File.Move(path, rotated);
        File.WriteAllText(path, BepInExLines(2, "new"), new UTF8Encoding(false));
        release.Set();

        var first = await task;
        Assert.All(first.Entries, entry => Assert.Contains("old", entry.Raw));
        Assert.DoesNotContain(first.Entries, entry => entry.Raw.Contains("new", StringComparison.Ordinal));

        var second = await new LocalLogReader().ReadAsync(Source(path));
        Assert.Equal(2, second.Entries.Count);
        Assert.All(second.Entries, entry => Assert.Contains("new", entry.Raw));
    }

    [Fact]
    public async Task ATruncatedFileDoesNotLeakNewContentIntoTheSameRead()
    {
        var path = Write("truncate.log", BepInExLines(3, "old"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new LocalLogReader
        {
            AfterProbeHook = () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var task = Task.Run(() => reader.ReadAsync(Source(path)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "reader never reached the probe seam");

        using (var writer = new FileStream(
                   path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.SetLength(0);
            var replacement = Encoding.UTF8.GetBytes(BepInExLines(2, "new"));
            writer.Write(replacement);
        }

        release.Set();

        var first = await task;
        Assert.DoesNotContain(first.Entries, entry => entry.Raw.Contains("new", StringComparison.Ordinal));

        var second = await new LocalLogReader().ReadAsync(Source(path));
        Assert.Equal(2, second.Entries.Count);
        Assert.All(second.Entries, entry => Assert.Contains("new", entry.Raw));
    }

    [Fact]
    public async Task ReadsWhileAnotherHandleKeepsTheFileOpen()
    {
        var path = Write("locked.log", BepInExLines(3));
        await using var holder = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var result = await new LocalLogReader().ReadAsync(Source(path));

        Assert.Equal(LogReadOutcome.Ok, result.Outcome);
        Assert.Equal(3, result.Entries.Count);
    }

    [Fact]
    public async Task UnreadablePathReturnsFailed()
    {
        var reader = new LocalLogReader();
        var result = await reader.ReadAsync(
            new LogSource(SourceKind.ManualFile, "manual:dir", _directory));

        Assert.Equal(LogReadOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf16TailWindowIsDetectedWithoutTheBomInTheWindow(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        var text = string.Join('\n',
            Enumerable.Range(0, 400).Select(i => $"[Info   : BepInEx] 条目 {i} 填充填充填充填充填充填充"));
        var path = Write("utf16-tail.log", text, encoding);

        var reader = new LocalLogReader(new LocalLogReaderOptions(MaxBytes: 2048));
        var result = await reader.ReadAsync(Source(path));

        Assert.Equal(LogReadOutcome.Ok, result.Outcome);
        Assert.NotEmpty(result.Entries);
        Assert.Contains("条目 399", result.Entries[^1].Message);
        Assert.All(result.Entries, entry => Assert.DoesNotContain('\uFFFD', entry.Message));
        Assert.True(result.BytesRead <= 2048 + LocalLogReader.ReadOverheadBytes, $"read {result.BytesRead} bytes");
    }

    [Fact]
    public async Task AnOversizedEntryKeepsItsTail()
    {
        var path = Write("long-tail.log", $"[Info   : BepInEx] {new string('x', 500)}TAILMARK");
        var reader = new LocalLogReader(new LocalLogReaderOptions(MaxEntryChars: 50));

        var result = await reader.ReadAsync(Source(path));

        Assert.True(result.Truncation.HasFlag(LogTruncation.EntryLength));
        Assert.EndsWith("TAILMARK", result.Entries[0].Message);
        Assert.True(result.Entries[0].Message.Length <= 50);
    }

    [Fact]
    public async Task DecodeFallbackIsReportedOnlyWhenBytesWereReplaced()
    {
        var broken = Path.Combine(_directory, "broken-flag.log");
        File.WriteAllBytes(broken, Encoding.ASCII.GetBytes("[Error  : Unity Log] bad ")
            .Concat(new byte[] { 0xC3, 0x28 })
            .Concat(Encoding.ASCII.GetBytes(" bytes"))
            .ToArray());

        var valid = Write("valid-replacement.log", "[Info   : BepInEx] literal \uFFFD stays");

        var brokenResult = await new LocalLogReader().ReadAsync(Source(broken));
        var validResult = await new LocalLogReader().ReadAsync(Source(valid));

        Assert.True(brokenResult.HadDecodeFallback);
        Assert.False(validResult.HadDecodeFallback);
        Assert.Contains('\uFFFD', validResult.Entries[0].Message);
    }

    [Fact]
    public async Task StartOffsetsStayStableWhenTheTailWindowMoves()
    {
        var fillerLine = "[Info   : BepInEx] " + new string('a', 80) + "\n";
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 25000; i++) builder.Append(fillerLine);
        builder.Append("[Info   : BepInEx] MARKER-UNIQUE\n");
        for (var i = 0; i < 1000; i++) builder.Append(fillerLine);

        var path = Write("growing.log", builder.ToString());
        var reader = new LocalLogReader(new LocalLogReaderOptions(
            MaxBytes: 2 * 1024 * 1024,
            MaxEntries: 50000));

        var first = await reader.ReadAsync(Source(path));
        var firstMarker = Assert.Single(first.Entries, entry => entry.Message.Contains("MARKER-UNIQUE"));

        await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            var block = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(fillerLine, 10000)));
            await append.WriteAsync(block);
        }

        var second = await reader.ReadAsync(Source(path));
        var secondMarker = Assert.Single(second.Entries, entry => entry.Message.Contains("MARKER-UNIQUE"));

        Assert.Equal(firstMarker.StartOffset, secondMarker.StartOffset);
        Assert.True(firstMarker.StartOffset > 0);
    }

    [Fact]
    public async Task StartOffsetsStayStableForCrlfFilesWhenTheWindowMoves()
    {
        var fillerLine = "[Info   : BepInEx] " + new string('a', 80) + "\r\n";
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 25000; i++) builder.Append(fillerLine);
        builder.Append("[Info   : BepInEx] CRLF-MARKER\r\n");
        for (var i = 0; i < 1000; i++) builder.Append(fillerLine);

        var path = Write("growing-crlf.log", builder.ToString());
        var reader = new LocalLogReader(new LocalLogReaderOptions(
            MaxBytes: 2 * 1024 * 1024,
            MaxEntries: 50000));

        var first = await reader.ReadAsync(Source(path));
        var firstMarker = Assert.Single(first.Entries, entry => entry.Message.Contains("CRLF-MARKER"));

        await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            var block = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(fillerLine, 10000)));
            await append.WriteAsync(block);
        }

        var second = await reader.ReadAsync(Source(path));
        var secondMarker = Assert.Single(second.Entries, entry => entry.Message.Contains("CRLF-MARKER"));

        Assert.Equal(firstMarker.StartOffset, secondMarker.StartOffset);
        Assert.Equal("CRLF-MARKER", secondMarker.Message.TrimEnd('\r'));
    }

    [Fact]
    public async Task StartOffsetUsesRawBytesWhenTheDroppedHeadHasInvalidBytes()
    {
        var prefix = new System.Text.StringBuilder();
        for (var i = 0; i < 40; i++) prefix.Append("[Info   : BepInEx] filler ").Append(i).Append('\n');

        var prefixBytes = Encoding.UTF8.GetBytes(prefix.ToString());
        var broken = new byte[] { 0xFF };
        var brokenTail = Encoding.ASCII.GetBytes(" broken tail\n");
        var markerBytes = Encoding.UTF8.GetBytes("[Info   : BepInEx] BAD-MARKER\n");
        var after = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat("[Info   : BepInEx] after filler\n", 40)));

        var bytes = prefixBytes.Concat(broken).Concat(brokenTail).Concat(markerBytes).Concat(after).ToArray();
        var brokenStart = prefixBytes.Length;
        var markerStart = brokenStart + broken.Length + brokenTail.Length;

        var path = Path.Combine(_directory, "broken-head.log");
        File.WriteAllBytes(path, bytes);

        var reader = new LocalLogReader(new LocalLogReaderOptions(
            MaxBytes: bytes.Length - (brokenStart - 1),
            MaxEntries: 5000));

        var result = await reader.ReadAsync(Source(path));

        var marker = Assert.Single(result.Entries, entry => entry.Message.Contains("BAD-MARKER"));
        Assert.Equal(markerStart, marker.StartOffset);
        Assert.True(result.HadDecodeFallback);
    }

    [Fact]
    public async Task BytesReadStaysWithinTheWindowBudget()
    {
        var path = Write("budget.log", string.Concat(Enumerable.Repeat(BepInExLines(1) + "\n", 500)));
        var reader = new LocalLogReader(new LocalLogReaderOptions(MaxBytes: 2048, MaxEntries: 5000));

        var result = await reader.ReadAsync(Source(path));

        Assert.True(result.BytesRead > 0);
        Assert.True(
            result.BytesRead <= 2048 + LocalLogReader.ReadOverheadBytes,
            $"read {result.BytesRead} bytes");
    }

    [Fact]
    public async Task TruncationReportsTheActualReason()
    {
        var headPath = Write("reason-head.log", string.Concat(Enumerable.Repeat(BepInExLines(1) + "\n", 500)));
        var head = await new LocalLogReader(new LocalLogReaderOptions(MaxBytes: 2048))
            .ReadAsync(Source(headPath));

        var countPath = Write("reason-count.log", BepInExLines(10));
        var count = await new LocalLogReader(new LocalLogReaderOptions(MaxEntries: 3))
            .ReadAsync(Source(countPath));

        var lengthPath = Write("reason-length.log", $"[Info   : BepInEx] {new string('y', 300)}");
        var length = await new LocalLogReader(new LocalLogReaderOptions(MaxEntryChars: 50))
            .ReadAsync(Source(lengthPath));

        Assert.True(head.Truncation.HasFlag(LogTruncation.Head));
        Assert.False(head.Truncation.HasFlag(LogTruncation.EntryCount));
        Assert.False(head.Truncation.HasFlag(LogTruncation.EntryLength));

        Assert.True(count.Truncation.HasFlag(LogTruncation.EntryCount));
        Assert.False(count.Truncation.HasFlag(LogTruncation.Head));

        Assert.True(length.Truncation.HasFlag(LogTruncation.EntryLength));
        Assert.False(length.Truncation.HasFlag(LogTruncation.Head));
    }
}

public sealed class LogSourceLocationsTests
{
    [Fact]
    public void BepInExSourcePointsAtTheInstanceLog()
    {
        var source = LogSourceLocations.BepInEx(@"C:\Games\Casualties Unknown", "abc123");

        Assert.Equal(SourceKind.BepInEx, source.Kind);
        Assert.Equal(@"C:\Games\Casualties Unknown\BepInEx\LogOutput.log", source.Path);
        Assert.Equal("bepinex:abc123", source.Identity);
        Assert.Equal("abc123", source.InstanceKey);
    }

    [Fact]
    public void UnitySourcesUseTheCompanyAndProductFolders()
    {
        var current = LogSourceLocations.UnityCurrent(@"C:\Users\x\AppData\LocalLow");
        var previous = LogSourceLocations.UnityPrevious(@"C:\Users\x\AppData\LocalLow");

        Assert.Equal(
            @"C:\Users\x\AppData\LocalLow\Orsoniks\CasualtiesUnknown\Player.log",
            current.Path);
        Assert.Equal(
            @"C:\Users\x\AppData\LocalLow\Orsoniks\CasualtiesUnknown\Player-prev.log",
            previous.Path);
        Assert.Equal(SourceKind.UnityCurrent, current.Kind);
        Assert.Equal(SourceKind.UnityPrevious, previous.Kind);
        Assert.NotEqual(current.Identity, previous.Identity);
    }

    [Fact]
    public void ManualSourceNormalizesThePath()
    {
        var source = LogSourceLocations.Manual(@"logs\..\other.log");

        Assert.Equal(SourceKind.ManualFile, source.Kind);
        Assert.True(Path.IsPathFullyQualified(source.Path!));
        Assert.StartsWith("manual:", source.Identity);
    }
}
