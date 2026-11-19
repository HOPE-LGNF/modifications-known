using ModificationsKnown.Core.Diagnostics.Logs;

namespace ModificationsKnown.Tests;

public sealed class LogViewerCoordinatorTests
{
    private static LogSource BepInEx(string instance = "inst-a") =>
        new(SourceKind.BepInEx, $"bepinex:{instance}", @"C:\game\BepInEx\LogOutput.log", instance);

    private static LogSource Unity() =>
        new(SourceKind.UnityCurrent, @"unity:current:C:\LocalLow\Orsoniks\CasualtiesUnknown\Player.log",
            @"C:\LocalLow\Orsoniks\CasualtiesUnknown\Player.log");

    private static LogEntry Entry(LogLevel level, string message) =>
        new(level, level.ToString(), "BepInEx", message, message, 0);

    private static LogReadResult Ok(params LogEntry[] entries) => LogReadResult.Ok(entries);

    [Fact]
    public async Task LoadWithoutASourceReturnsAStructuredError()
    {
        var coordinator = new LogViewerCoordinator(new StubReader());

        var result = await coordinator.LoadAsync();

        Assert.False(result.Adopted);
        Assert.Equal(LogReadOutcome.Failed, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(LogLoadError.SourceNotSelected, result.ErrorKind);
    }

    [Fact]
    public async Task LoadStoresASnapshotWithTheSourceIdentity()
    {
        var reader = new StubReader { Result = Ok(Entry(LogLevel.Info, "hello")) };
        var coordinator = new LogViewerCoordinator(reader, () => DateTimeOffset.UnixEpoch);
        coordinator.SetSource(BepInEx());

        var result = await coordinator.LoadAsync();

        Assert.True(result.Adopted);
        Assert.Equal(LogReadOutcome.Ok, result.Outcome);
        Assert.NotNull(coordinator.Snapshot);
        Assert.Equal("bepinex:inst-a", coordinator.Snapshot!.Source.Identity);
        Assert.Equal(DateTimeOffset.UnixEpoch, coordinator.Snapshot.ReadAt);
        Assert.Single(coordinator.Snapshot.Entries);
    }

    [Fact]
    public async Task ChangingSourceDropsTheSnapshotAndTheGeneration()
    {
        var reader = new StubReader { Result = Ok(Entry(LogLevel.Info, "hello")) };
        var coordinator = new LogViewerCoordinator(reader);
        coordinator.SetSource(BepInEx());
        await coordinator.LoadAsync();
        var generation = coordinator.Generation;

        var changed = coordinator.SetSource(Unity());

        Assert.True(changed);
        Assert.Null(coordinator.Snapshot);
        Assert.True(coordinator.Generation > generation);
    }

    [Fact]
    public async Task SelectingTheSameSourceKeepsTheSnapshot()
    {
        var reader = new StubReader { Result = Ok(Entry(LogLevel.Info, "hello")) };
        var coordinator = new LogViewerCoordinator(reader);
        coordinator.SetSource(BepInEx());
        await coordinator.LoadAsync();

        Assert.False(coordinator.SetSource(BepInEx()));
        Assert.NotNull(coordinator.Snapshot);
    }

    [Fact]
    public async Task LateLoadIsDiscardedAfterASourceChange()
    {
        var pending = new TaskCompletionSource<LogReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new StubReader { Pending = pending };
        var coordinator = new LogViewerCoordinator(reader);
        coordinator.SetSource(BepInEx());

        var load = coordinator.LoadAsync();
        coordinator.SetSource(Unity());
        pending.SetResult(Ok(Entry(LogLevel.Error, "late")));

        var result = await load;

        Assert.False(result.Adopted);
        Assert.Null(coordinator.Snapshot);
    }

    [Fact]
    public async Task FailedRefreshKeepsThePreviousSnapshot()
    {
        var reader = new StubReader { Result = Ok(Entry(LogLevel.Info, "first")) };
        var coordinator = new LogViewerCoordinator(reader);
        coordinator.SetSource(BepInEx());
        await coordinator.LoadAsync();
        var first = coordinator.Snapshot;

        reader.Result = LogReadResult.Failed("access denied");
        var result = await coordinator.LoadAsync();

        Assert.False(result.Adopted);
        Assert.True(result.KeptPreviousSnapshot);
        Assert.Same(first, coordinator.Snapshot);
    }

    [Fact]
    public async Task FailureAfterASourceChangeLeavesNoSnapshot()
    {
        var reader = new StubReader { Result = LogReadResult.Failed("boom") };
        var coordinator = new LogViewerCoordinator(reader);
        coordinator.SetSource(Unity());

        var result = await coordinator.LoadAsync();

        Assert.False(result.Adopted);
        Assert.False(result.KeptPreviousSnapshot);
        Assert.Null(coordinator.Snapshot);
    }

    [Fact]
    public async Task MissingFileIsNotTreatedAsAnError()
    {
        var reader = new StubReader { Result = LogReadResult.Missing() };
        var coordinator = new LogViewerCoordinator(reader);
        coordinator.SetSource(Unity());

        var result = await coordinator.LoadAsync();

        Assert.True(result.Adopted);
        Assert.Equal(LogReadOutcome.Missing, result.Outcome);
        Assert.NotNull(coordinator.Snapshot);
        Assert.Empty(coordinator.Snapshot!.Entries);
    }

    [Fact]
    public async Task FilterUsesTheCurrentSnapshot()
    {
        var reader = new StubReader
        {
            Result = Ok(
                Entry(LogLevel.Info, "loading"),
                Entry(LogLevel.Error, "boom"))
        };
        var coordinator = new LogViewerCoordinator(reader);
        coordinator.SetSource(BepInEx());
        await coordinator.LoadAsync();

        Assert.Single(coordinator.Filter(LogLevelFilter.Errors, LogSearchScope.All, null));
        Assert.Empty(coordinator.Filter(LogLevelFilter.All, LogSearchScope.All, "missing"));
    }

    [Fact]
    public void FilterWithoutSnapshotReturnsEmpty()
    {
        var coordinator = new LogViewerCoordinator(new StubReader());

        Assert.Empty(coordinator.Filter(LogLevelFilter.All, LogSearchScope.All, null));
    }

    [Fact]
    public async Task LoadWithoutSourceIsRejected()
    {
        var reader = new StubReader { Result = Ok() };
        var coordinator = new LogViewerCoordinator(reader);

        var result = await coordinator.LoadAsync();

        Assert.False(result.Adopted);
        Assert.Equal(0, reader.Calls);
    }

    private sealed class StubReader : ILogReader
    {
        public LogReadResult Result { get; set; } = LogReadResult.Ok(Array.Empty<LogEntry>());

        public TaskCompletionSource<LogReadResult>? Pending { get; set; }

        public int Calls { get; private set; }

        public Task<LogReadResult> ReadAsync(LogSource source, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Pending is null ? Task.FromResult(Result) : Pending.Task;
        }
    }
}
