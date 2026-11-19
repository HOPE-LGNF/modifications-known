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
