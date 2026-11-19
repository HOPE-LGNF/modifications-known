using ModificationsKnown.App.Services;
using ModificationsKnown.App.ViewModels;
using ModificationsKnown.Core.Diagnostics.Logs;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.ViewModels.Tests;

/// <summary>Snapshot/source-switch state machine of the log page. No Avalonia
/// platform, no disk, no real dialogs.</summary>
public sealed class LogViewerSnapshotStateTests
{
    private static LogSource BepInExSource() =>
        new(SourceKind.BepInEx, @"bepinex:C:\game-a", @"C:\game-a\BepInEx\LogOutput.log", @"C:\game-a");

    private static LogSource UnitySource() =>
        new(SourceKind.UnityCurrent, @"unity:current:C:\LocalLow\Player.log", @"C:\LocalLow\Player.log");

    private static LogEntry Entry(LogLevel level, string message, string source = "BepInEx") =>
        new(level, level.ToString(), source, message, message, 0);

    private static LogReadResult Ok(params LogEntry[] entries) =>
        LogReadResult.Ok(entries);

    private static (LogViewerViewModel Vm, ScriptedReader Reader, StubProvider Provider) Build()
    {
        var reader = new ScriptedReader();
        var provider = new StubProvider([BepInExSource(), UnitySource()]);
        var vm = new LogViewerViewModel(
            new StubShell(),
            provider,
            new StubPicker(),
            new StubClipboard(),
            new StubLauncher(),
            reader)
        {
            SearchDebounce = TimeSpan.Zero
        };

        return (vm, reader, provider);
    }

    private static LogSourceOption Option(LogViewerViewModel vm, SourceKind kind) =>
        vm.Sources.Single(option => option.Source.Kind == kind);

    [Fact]
    public async Task SwitchingSourceClearsRowsAndCopyBeforeTheNewReadCompletes()
    {
        var (vm, reader, _) = Build();
        var gate = new TaskCompletionSource<LogReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Handlers[SourceKind.BepInEx] = _ => Task.FromResult(Ok(Entry(LogLevel.Info, "old row")));
        reader.Handlers[SourceKind.UnityCurrent] = _ => gate.Task;

        await vm.OnActivatedAsync();
        Assert.Single(vm.Entries);

        vm.SelectedSource = Option(vm, SourceKind.UnityCurrent);

        Assert.Empty(vm.Entries);
        Assert.False(vm.CopyFilteredCommand.CanExecute(null));
        Assert.False(vm.CopyEntryCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.ReadAsOfText);
        Assert.Equal(string.Empty, vm.TruncatedText);

        gate.SetResult(Ok(Entry(LogLevel.Info, "new row", "Unity Log")));
        await vm.WaitForLoadAsync();

        Assert.Single(vm.Entries);
        Assert.Equal("new row", vm.Entries[0].Raw);
    }

    [Fact]
    public async Task SwitchingToAFailingSourceLeavesNoOldRowsOrTimestamp()
    {
        var (vm, reader, _) = Build();
        reader.Handlers[SourceKind.BepInEx] = _ => Task.FromResult(Ok(Entry(LogLevel.Info, "old row")));
        reader.Handlers[SourceKind.UnityCurrent] = _ => Task.FromResult(LogReadResult.Failed("denied"));

        await vm.OnActivatedAsync();
        Assert.Single(vm.Entries);
        Assert.NotEqual(string.Empty, vm.ReadAsOfText);

        vm.SelectedSource = Option(vm, SourceKind.UnityCurrent);
        await vm.WaitForLoadAsync();

        Assert.Empty(vm.Entries);
        Assert.Equal(string.Empty, vm.ReadAsOfText);
        Assert.Equal(string.Empty, vm.TruncatedText);
        Assert.Contains("读取失败：denied", vm.StatusText);
        Assert.False(vm.CopyFilteredCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshingTheSameSourceKeepsRowsAndShowsTheRefreshingState()
    {
        var (vm, reader, _) = Build();
        var gate = new TaskCompletionSource<LogReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        reader.Handlers[SourceKind.BepInEx] = _ =>
        {
            calls++;
            return calls == 1
                ? Task.FromResult(Ok(Entry(LogLevel.Info, "first")))
                : gate.Task;
        };

        await vm.OnActivatedAsync();
        var timestamp = vm.ReadAsOfText;

        var refresh = vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Entries);
        Assert.Contains("正在刷新", vm.StatusText);
        Assert.Equal(timestamp, vm.ReadAsOfText);

        gate.SetResult(Ok(Entry(LogLevel.Info, "second")));
        await refresh;
        await vm.WaitForLoadAsync();

        Assert.Single(vm.Entries);
        Assert.Equal("second", vm.Entries[0].Raw);
    }

    [Fact]
    public async Task RefreshFailureKeepsTheOldSnapshotAndItsTimestamp()
    {
        var (vm, reader, _) = Build();
        var calls = 0;
        reader.Handlers[SourceKind.BepInEx] = _ =>
        {
            calls++;
            return calls == 1
                ? Task.FromResult(Ok(Entry(LogLevel.Info, "first")))
                : Task.FromResult(LogReadResult.Failed("denied"));
        };

        await vm.OnActivatedAsync();
        var timestamp = vm.ReadAsOfText;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Entries);
        Assert.Equal(timestamp, vm.ReadAsOfText);
        Assert.Contains("刷新失败：denied", vm.StatusText);
        Assert.Contains("仍显示上次读取的内容", vm.StatusText);
    }

    [Fact]
    public async Task ReactivationDoesNotReadAgainWhenASnapshotExists()
    {
        var (vm, reader, _) = Build();
        reader.Handlers[SourceKind.BepInEx] = _ => Task.FromResult(Ok(Entry(LogLevel.Info, "row")));

        await vm.OnActivatedAsync();
        Assert.Equal(1, reader.Calls);

        await vm.OnActivatedAsync();
        Assert.Equal(1, reader.Calls);

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, reader.Calls);
    }

    [Fact]
    public async Task ReactivationAfterAMissingSnapshotDoesNotReadAgain()
    {
        var (vm, reader, _) = Build();
        reader.Handlers[SourceKind.BepInEx] = _ => Task.FromResult(LogReadResult.Missing());

        await vm.OnActivatedAsync();
        Assert.Equal(1, reader.Calls);
        Assert.Contains("尚未找到日志", vm.StatusText);

        await vm.OnActivatedAsync();
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task RepeatedRefreshClicksAreIgnoredWhileTheFirstIsRunning()
    {
        var (vm, reader, _) = Build();
        var gate = new TaskCompletionSource<LogReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        reader.Handlers[SourceKind.BepInEx] = _ =>
        {
            calls++;
            return calls == 1 ? Task.FromResult(Ok(Entry(LogLevel.Info, "row"))) : gate.Task;
        };

        await vm.OnActivatedAsync();

        var first = vm.RefreshCommand.ExecuteAsync(null);
        var second = vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, reader.Calls);
        Assert.True(vm.RefreshCommand.CanExecute(null));

        gate.SetResult(Ok(Entry(LogLevel.Info, "refreshed")));
        await Task.WhenAll(first, second);
        await vm.WaitForLoadAsync();

        Assert.Equal(2, reader.Calls);
        Assert.Single(vm.Entries);
    }

    [Fact]
    public async Task CopyHintMentionsSensitiveInformation()
    {
        var (vm, _, _) = Build();
        await vm.OnActivatedAsync();

        Assert.Contains("敏感", vm.CopyHint);
    }

    [Fact]
    public async Task TruncationTextNamesTheRealReason()
    {
        var (headVm, headReader, _) = Build();
        headReader.Handlers[SourceKind.BepInEx] =
            _ => Task.FromResult(LogReadResult.Ok([Entry(LogLevel.Info, "x")], LogTruncation.Head));
        await headVm.OnActivatedAsync();
        Assert.Contains("文件尾部", headVm.TruncatedText);

        var (countVm, countReader, _) = Build();
        countReader.Handlers[SourceKind.BepInEx] =
            _ => Task.FromResult(LogReadResult.Ok([Entry(LogLevel.Info, "x")], LogTruncation.EntryCount));
        await countVm.OnActivatedAsync();
        Assert.Contains("条目", countVm.TruncatedText);
        Assert.DoesNotContain("文件尾部", countVm.TruncatedText);

        var (lengthVm, lengthReader, _) = Build();
        lengthReader.Handlers[SourceKind.BepInEx] =
            _ => Task.FromResult(LogReadResult.Ok([Entry(LogLevel.Info, "x")], LogTruncation.EntryLength));
        await lengthVm.OnActivatedAsync();
        Assert.Contains("单条", lengthVm.TruncatedText);
        Assert.DoesNotContain("文件尾部", lengthVm.TruncatedText);
    }

    [Fact]
    public async Task DecodeFallbackAddsAPersistentNote()
    {
        var (vm, reader, _) = Build();
        reader.Handlers[SourceKind.BepInEx] = _ =>
            Task.FromResult(LogReadResult.Ok([Entry(LogLevel.Info, "x")], LogTruncation.None, hadDecodeFallback: true));

        await vm.OnActivatedAsync();

        Assert.Contains("无法解码", vm.DecodeNote);
    }

    [Fact]
    public async Task LateResultFromThePreviousSourceDoesNotOverwriteTheNewSource()
    {
        var (vm, reader, _) = Build();
        var gate = new TaskCompletionSource<LogReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Handlers[SourceKind.BepInEx] = _ => gate.Task;
        reader.Handlers[SourceKind.UnityCurrent] =
            _ => Task.FromResult(Ok(Entry(LogLevel.Info, "B row", "Unity Log")));

        var activation = vm.OnActivatedAsync();
        var firstLoad = vm.WaitForLoadAsync();

        vm.SelectedSource = Option(vm, SourceKind.UnityCurrent);
        await vm.WaitForLoadAsync();
        Assert.Single(vm.Entries);
        Assert.Equal("B row", vm.Entries[0].Raw);

        gate.SetResult(Ok(Entry(LogLevel.Info, "A late row")));
        await firstLoad;
        await activation;

        Assert.Single(vm.Entries);
        Assert.Equal("B row", vm.Entries[0].Raw);
        Assert.DoesNotContain("A late row", vm.StatusText);
    }

    [Fact]
    public async Task RemovingTheInstanceCancelsTheInFlightReadAndKeepsTheGuidance()
    {
        var (vm, reader, provider) = Build();
        var gate = new TaskCompletionSource<LogReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Handlers[SourceKind.BepInEx] = _ => gate.Task;

        var activation = vm.OnActivatedAsync();
        var firstLoad = vm.WaitForLoadAsync();

        provider.Items.Clear();
        provider.Items.Add(new LogSource(SourceKind.BepInEx, "bepinex:none"));
        provider.Items.Add(UnitySource());
        await vm.OnInstanceChangedAsync();

        gate.SetResult(Ok(Entry(LogLevel.Info, "late old row")));
        await firstLoad;
        await activation;

        Assert.Empty(vm.Entries);
        Assert.Equal("请选择一个游戏实例，或切换到 Unity 日志 / 选择其他日志文件。", vm.StatusText);
    }

    [Fact]
    public async Task DisposeCancelsTheInFlightReadAndDropsItsResult()
    {
        var (vm, reader, _) = Build();
        var gate = new TaskCompletionSource<LogReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Handlers[SourceKind.BepInEx] = _ => gate.Task;

        var loading = vm.OnActivatedAsync();
        vm.Dispose();

        Assert.True(reader.LastCancellationToken.IsCancellationRequested);

        gate.SetResult(Ok(Entry(LogLevel.Info, "late row")));
        await loading;

        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task ProviderEventsAfterDisposeDoNotReload()
    {
        var (vm, reader, provider) = Build();
        reader.Handlers[SourceKind.BepInEx] = _ => Task.FromResult(Ok(Entry(LogLevel.Info, "row")));
        await vm.OnActivatedAsync();
        var calls = reader.Calls;

        vm.Dispose();
        provider.Raise();

        Assert.Equal(calls, reader.Calls);
    }

    private sealed class ScriptedReader : ILogReader
    {
        public Dictionary<SourceKind, Func<LogSource, Task<LogReadResult>>> Handlers { get; } = new();

        public int Calls { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<LogReadResult> ReadAsync(LogSource source, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCancellationToken = cancellationToken;
            return Handlers.TryGetValue(source.Kind, out var handler)
                ? handler(source)
                : Task.FromResult(LogReadResult.Ok(Array.Empty<LogEntry>()));
        }
    }

    private sealed class StubProvider : ILogSourceProvider
    {
        public StubProvider(IReadOnlyList<LogSource> sources) => Items = sources.ToList();

        public List<LogSource> Items { get; }

        public event EventHandler? SourcesChanged;

        public IReadOnlyList<LogSource> Describe() => Items.ToList();

        public void Raise() => SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class StubClipboard : IClipboardService
    {
        public Task<bool> SetTextAsync(string text) => Task.FromResult(true);
    }

    private sealed class StubLauncher : IExternalLauncher
    {
        public bool TryOpenFile(string path, out string? error)
        {
            error = null;
            return true;
        }

        public bool TryOpenFolder(string path, out string? error)
        {
            error = null;
            return true;
        }
    }

    private sealed class StubPicker : IFilePickerService
    {
        public Task<string?> PickExecutableAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickArchiveAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickThemeFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickLogFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickDataFolderAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickBackupFolderAsync() => Task.FromResult<string?>(null);
    }

    private sealed class StubShell : IAppShell
    {
        public GameContext Game => null!;

        public IAppSettingsService Settings => null!;

        public void SetStatus(string message, StatusKind kind = StatusKind.Info)
        {
        }

        public Task RefreshGameAsync() => Task.CompletedTask;

        public bool CanLaunch => false;

        public string LaunchButtonText => string.Empty;

        public bool IsCloseAction => false;

        public Task LaunchGameAsync() => Task.CompletedTask;

        public event Action<IReadOnlyCollection<string>>? ModsChanged
        {
            add { }
            remove { }
        }

        public void NotifyModsChanged(IReadOnlyCollection<string> packageNames)
        {
        }

        public void NavigateTo(string key)
        {
        }

        public void NavigateSettings(string focus)
        {
        }
    }
}
