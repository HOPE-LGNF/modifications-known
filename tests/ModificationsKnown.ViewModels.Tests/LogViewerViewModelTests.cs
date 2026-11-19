using ModificationsKnown.App.Services;
using ModificationsKnown.App.ViewModels;
using ModificationsKnown.Core.Diagnostics.Logs;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.ViewModels.Tests;

/// <summary>View-model behaviour only: no Avalonia platform, no disk, no real
/// dialogs. The real View binding still needs manual acceptance (no App.Tests).</summary>
public sealed class LogViewerViewModelTests
{
    private const string TimestampPattern = @"^读取于 \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{2}:\d{2}$";

    private static LogSource BepInExSource(string directory) =>
        new(SourceKind.BepInEx, $"bepinex:{directory}", Path.Combine(directory, "BepInEx", "LogOutput.log"), directory);

    private static LogSource UnitySource(SourceKind kind = SourceKind.UnityCurrent) =>
        new(kind, "unity:current:C:\\LocalLow\\Orcas", @"C:\LocalLow\Player.log");

    private static LogEntry Entry(LogLevel level, string message, string source = "BepInEx", long offset = 0) =>
        new(level, level.ToString(), source, message, message, offset);

    private static LogReadResult Ok(params LogEntry[] entries) =>
        LogReadResult.Ok(entries);

    private static (LogViewerViewModel Vm, StubReader Reader, StubClipboard Clipboard, StubLauncher Launcher, StubShell Shell, StubSourceProvider Provider)
        Build(LogReadResult bepInExResult, params LogSource[] sources)
    {
        var reader = new StubReader();
        reader.ByKind[SourceKind.BepInEx] = bepInExResult;
        var provider = new StubSourceProvider(sources.Length == 0
            ? [BepInExSource(@"C:\game-a"), UnitySource()]
            : sources);

        return BuildWith(provider, reader);
    }

    private static (LogViewerViewModel Vm, StubReader Reader, StubClipboard Clipboard, StubLauncher Launcher, StubShell Shell, StubSourceProvider Provider)
        BuildWith(StubSourceProvider provider, StubReader reader)
    {
        var clipboard = new StubClipboard();
        var launcher = new StubLauncher();
        var shell = new StubShell();

        var vm = new LogViewerViewModel(
            shell,
            provider,
            new StubFilePicker(),
            clipboard,
            launcher,
            reader)
        {
            SearchDebounce = TimeSpan.Zero
        };

        return (vm, reader, clipboard, launcher, shell, provider);
    }

    [Fact]
    public async Task UnsupportedManualLogFileIsRejectedAndKeepsTheSnapshot()
    {
        var reader = new StubReader();
        reader.ByKind[SourceKind.BepInEx] = Ok(Entry(LogLevel.Info, "kept"));
        var provider = new StubSourceProvider([BepInExSource(@"C:\game-a"), UnitySource()]);
        var picker = new StubFilePicker { LogPath = @"C:\tmp\notes.md" };
        var shell = new StubShell();
        var vm = new LogViewerViewModel(
            shell, provider, picker, new StubClipboard(), new StubLauncher(), reader)
        {
            SearchDebounce = TimeSpan.Zero
        };

        await vm.OnActivatedAsync();
        var source = vm.SelectedSource;

        await vm.SelectLogFileCommand.ExecuteAsync(null);

        Assert.Same(source, vm.SelectedSource);
        Assert.Single(vm.Entries);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(StatusKind.Warning, shell.LastKind);
        Assert.Contains(".log", shell.LastStatus);
        Assert.Contains(".txt", shell.LastStatus);
    }

    [Fact]
    public async Task SupportedManualLogFileIsAcceptedWithAnUppercaseExtension()
    {
        var reader = new StubReader();
        reader.ByKind[SourceKind.BepInEx] = Ok(Entry(LogLevel.Info, "old"));
        reader.ByKind[SourceKind.ManualFile] = Ok(Entry(LogLevel.Info, "manual row", offset: 7));
        var provider = new StubSourceProvider([BepInExSource(@"C:\game-a"), UnitySource()]);
        var picker = new StubFilePicker { LogPath = @"C:\tmp\OLD.LOG" };
        var vm = new LogViewerViewModel(
            new StubShell(), provider, picker, new StubClipboard(), new StubLauncher(), reader)
        {
            SearchDebounce = TimeSpan.Zero
        };

        await vm.OnActivatedAsync();
        await vm.SelectLogFileCommand.ExecuteAsync(null);

        Assert.Equal(SourceKind.ManualFile, vm.SelectedSource!.Source.Kind);
        Assert.EndsWith("OLD.LOG", vm.SelectedSource.PathText);
        Assert.Equal("manual row", vm.Entries[0].Raw);
    }

    [Fact]
    public async Task ActivationLoadsTheFirstSource()
    {
        var (vm, reader, _, _, _, _) = Build(Ok(Entry(LogLevel.Info, "hello")));

        await vm.OnActivatedAsync();

        Assert.Equal(1, reader.Calls);
        Assert.Single(vm.Entries);
        Assert.True(vm.HasEntries);
        Assert.Equal("INFO", vm.Entries[0].LevelText);
        Assert.Contains("已读取 1 条日志", vm.StatusText);
        Assert.Matches(TimestampPattern, vm.ReadAsOfText);
        Assert.Equal("当前显示 1 条，共读取 1 条", vm.FilterSummary);
    }

    [Fact]
    public async Task MissingFileShowsTheGuidanceText()
    {
        var (vm, _, _, _, _, _) = Build(LogReadResult.Missing());

        await vm.OnActivatedAsync();

        Assert.False(vm.HasEntries);
        Assert.Equal("尚未找到日志。游戏运行后可能生成日志，也可以手动打开其他日志文件。", vm.StatusText);
        Assert.Equal(vm.StatusText, vm.EmptyMessage);
    }

    [Fact]
    public async Task OkSnapshotWithoutEntriesIsNotReportedAsAnEmptyFile()
    {
        var (vm, _, _, _, _, _) = Build(LogReadResult.Ok(
            Array.Empty<LogEntry>(), LogTruncation.Head));

        await vm.OnActivatedAsync();

        Assert.Empty(vm.Entries);
        Assert.NotEqual("日志为空。", vm.EmptyMessage);
        Assert.Contains("没有可显示的完整日志条目", vm.EmptyMessage);
        Assert.NotEqual(string.Empty, vm.TruncatedText);
    }

    [Fact]
    public async Task EmptyFileIsNotAnError()
    {
        var (vm, _, _, _, _, _) = Build(LogReadResult.Empty());

        await vm.OnActivatedAsync();

        Assert.Equal("日志为空。", vm.StatusText);
        Assert.False(vm.HasEntries);
    }

    [Fact]
    public async Task LevelFilterNarrowsTheRows()
    {
        var (vm, _, _, _, _, _) = Build(Ok(
            Entry(LogLevel.Info, "loading"),
            Entry(LogLevel.Warning, "warn"),
            Entry(LogLevel.Error, "boom")));

        await vm.OnActivatedAsync();
        Assert.Equal(3, vm.Entries.Count);

        vm.SelectedLevel = vm.LevelOptions.Single(option => option.Value == LogLevelFilter.Errors);

        Assert.Single(vm.Entries);
        Assert.Equal("ERROR", vm.Entries[0].LevelText);
        Assert.Equal("当前显示 1 条，共读取 3 条", vm.FilterSummary);
        Assert.Equal("复制列表日志（1 条）", vm.CopyFilteredText);
    }

    [Fact]
    public async Task SearchMatchesSourceAndMessageCaseInsensitively()
    {
        var (vm, _, _, _, _, _) = Build(Ok(
            Entry(LogLevel.Info, "Loading mods"),
            Entry(LogLevel.Error, "boom", "Unity Log")));

        await vm.OnActivatedAsync();

        vm.SearchText = "unity";
        Assert.Single(vm.Entries);

        vm.SearchText = "LOADING";
        Assert.Single(vm.Entries);

        vm.SearchText = "nothing";
        Assert.Empty(vm.Entries);
        Assert.Equal("没有匹配的日志。", vm.EmptyMessage);
    }

    [Fact]
    public async Task SourceScopeIgnoresTheMessageBody()
    {
        var (vm, _, _, _, _, _) = Build(Ok(
            Entry(LogLevel.Error, "NullReferenceException in Foo", "Unity Log")));

        await vm.OnActivatedAsync();
        vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == LogSearchScope.Source);

        vm.SearchText = "unity";
        Assert.Single(vm.Entries);

        vm.SearchText = "NullReference";
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task MessageScopeIgnoresTheSourceField()
    {
        var (vm, _, _, _, _, _) = Build(Ok(
            Entry(LogLevel.Error, "NullReferenceException in Foo", "Unity Log")));

        await vm.OnActivatedAsync();
        vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == LogSearchScope.Message);

        vm.SearchText = "NullReference";
        Assert.Single(vm.Entries);

        vm.SearchText = "unity";
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void SearchWatermarkFollowsTheScope()
    {
        var (vm, _, _, _, _, _) = Build(LogReadResult.Missing());

        Assert.Contains("日志来源或日志内容", vm.SearchWatermark);

        vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == LogSearchScope.Source);
        Assert.Contains("日志来源", vm.SearchWatermark);

        vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == LogSearchScope.Message);
        Assert.Contains("日志内容", vm.SearchWatermark);
    }

    [Fact]
    public void FirstVisitSelectsBepInExEvenWhenUnityIsListedFirst()
    {
        var reader = new StubReader();
        var provider = new StubSourceProvider([UnitySource(), BepInExSource(@"C:\game-a")]);

        var (vm, _, _, _, _, _) = BuildWith(provider, reader);

        Assert.Equal(SourceKind.BepInEx, vm.SelectedSource?.Source.Kind);
    }

    [Fact]
    public async Task ReturningToThePageKeepsTheSessionChoice()
    {
        var reader = new StubReader();
        reader.ByKind[SourceKind.BepInEx] = Ok(Entry(LogLevel.Info, "bep"));
        reader.ByKind[SourceKind.UnityCurrent] = Ok(Entry(LogLevel.Info, "unity", "Unity Log"));
        var provider = new StubSourceProvider([BepInExSource(@"C:\game-a"), UnitySource()]);

        var (vm, _, _, _, _, _) = BuildWith(provider, reader);
        await vm.OnActivatedAsync();

        vm.SelectedSource = vm.Sources.Single(option => option.Source.Kind == SourceKind.UnityCurrent);
        await vm.RefreshCurrentSourceAsync();

        await vm.OnActivatedAsync();

        Assert.Equal(SourceKind.UnityCurrent, vm.SelectedSource?.Source.Kind);
    }

    [Fact]
    public async Task MissingBepInExFileDoesNotFallBackToUnity()
    {
        var reader = new StubReader();
        reader.ByKind[SourceKind.BepInEx] = LogReadResult.Missing();
        reader.ByKind[SourceKind.UnityCurrent] = Ok(Entry(LogLevel.Info, "unity", "Unity Log"));
        var provider = new StubSourceProvider([BepInExSource(@"C:\game-a"), UnitySource()]);

        var (vm, _, _, _, _, _) = BuildWith(provider, reader);
        await vm.OnActivatedAsync();

        Assert.Equal(SourceKind.BepInEx, vm.SelectedSource?.Source.Kind);
        Assert.False(vm.HasEntries);
        Assert.Equal("尚未找到日志。游戏运行后可能生成日志，也可以手动打开其他日志文件。", vm.StatusText);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task NoInstanceKeepsBepInExSelectedAndReadsNothing()
    {
        var reader = new StubReader();
        var provider = new StubSourceProvider(
        [
            new LogSource(SourceKind.BepInEx, "bepinex:none"),
            UnitySource()
        ]);

        var (vm, _, _, _, _, _) = BuildWith(provider, reader);
        await vm.OnActivatedAsync();

        Assert.Equal(SourceKind.BepInEx, vm.SelectedSource?.Source.Kind);
        Assert.Equal(0, reader.Calls);
        Assert.False(vm.HasEntries);
        Assert.Equal("请选择一个游戏实例，或切换到 Unity 日志 / 选择其他日志文件。", vm.StatusText);
    }

    [Fact]
    public void WrapDetailSwitchesWrappingAndDisablesHorizontalScroll()
    {
        var (vm, _, _, _, _, _) = Build(LogReadResult.Missing());

        Assert.False(vm.WrapDetail);
        Assert.Equal(Avalonia.Media.TextWrapping.NoWrap, vm.DetailWrapping);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, vm.DetailHorizontalScroll);

        vm.WrapDetail = true;

        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, vm.DetailWrapping);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, vm.DetailHorizontalScroll);
    }

    [Fact]
    public async Task SelectingARowFillsTheDetailPane()
    {
        var (vm, _, _, _, _, _) = Build(Ok(Entry(LogLevel.Error, "boom\n  at Foo.Bar")));

        await vm.OnActivatedAsync();
        Assert.False(vm.HasSelection);

        vm.SelectedEntry = vm.Entries[0];

        Assert.True(vm.HasSelection);
        Assert.Contains("at Foo.Bar", vm.DetailText);
        Assert.True(vm.CopyEntryCommand.CanExecute(null));
        Assert.Equal("boom", vm.Entries[0].FirstLine);
    }

    [Fact]
    public async Task CopyFilteredCopiesEveryFilteredRowWithoutADialog()
    {
        var (vm, _, clipboard, _, _, _) = Build(Ok(
            Entry(LogLevel.Info, "keep me"),
            Entry(LogLevel.Error, "drop me")));

        await vm.OnActivatedAsync();
        vm.SelectedLevel = vm.LevelOptions.Single(option => option.Value == LogLevelFilter.Errors);

        await vm.CopyFilteredCommand.ExecuteAsync(null);

        Assert.Equal(1, clipboard.Calls);
        Assert.Contains("drop me", clipboard.Text);
        Assert.DoesNotContain("keep me", clipboard.Text);
    }

    [Fact]
    public async Task ClipboardFailureKeepsTheSnapshotAndReportsAnError()
    {
        var (vm, _, clipboard, _, shell, _) = Build(Ok(Entry(LogLevel.Info, "keep me")));
        clipboard.Result = false;

        await vm.OnActivatedAsync();
        await vm.CopyFilteredCommand.ExecuteAsync(null);

        Assert.Single(vm.Entries);
        Assert.Equal(StatusKind.Error, shell.LastKind);
        Assert.Contains("复制失败", shell.LastStatus);
    }

    [Fact]
    public async Task ClipboardExceptionKeepsTheSnapshotAndReportsAnError()
    {
        var (vm, _, clipboard, _, shell, _) = Build(Ok(Entry(LogLevel.Info, "keep me")));
        clipboard.Throw = true;

        await vm.OnActivatedAsync();
        await vm.CopyFilteredCommand.ExecuteAsync(null);

        Assert.Single(vm.Entries);
        Assert.Equal(StatusKind.Error, shell.LastKind);
        Assert.Contains("复制失败", shell.LastStatus);
    }

    [Fact]
    public async Task CopyFilteredIgnoresTheSelectionAndCopiesEveryMatch()
    {
        var (vm, _, clipboard, _, _, _) = Build(Ok(
            Entry(LogLevel.Info, "first"),
            Entry(LogLevel.Error, "second")));

        await vm.OnActivatedAsync();
        vm.SelectedEntry = vm.Entries[0];

        await vm.CopyFilteredCommand.ExecuteAsync(null);

        Assert.Contains("first", clipboard.Text);
        Assert.Contains("second", clipboard.Text);
        Assert.Equal("复制列表日志（2 条）", vm.CopyFilteredText);
    }

    [Fact]
    public async Task CopyEntryCopiesOnlyTheSelectedEntry()
    {
        var (vm, _, clipboard, _, _, _) = Build(Ok(
            Entry(LogLevel.Info, "first"),
            Entry(LogLevel.Error, "second")));

        await vm.OnActivatedAsync();
        vm.SelectedEntry = vm.Entries[1];

        await vm.CopyEntryCommand.ExecuteAsync(null);

        Assert.Contains("second", clipboard.Text);
        Assert.DoesNotContain("first", clipboard.Text);
    }

    [Fact]
    public async Task CopyHeaderCarriesTheSnapshotTimestamp()
    {
        var (vm, _, clipboard, _, _, _) = Build(Ok(Entry(LogLevel.Info, "x")));

        await vm.OnActivatedAsync();
        await vm.CopyFilteredCommand.ExecuteAsync(null);

        Assert.Contains("BepInEx 日志", clipboard.Text);
        Assert.Contains("读取于 20", clipboard.Text);
        Assert.Matches(@"读取于 \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{2}:\d{2}", clipboard.Text!);
    }

    [Fact]
    public void CopyIsDisabledWhenThereIsNothingToCopy()
    {
        var (vm, _, _, _, _, _) = Build(LogReadResult.Missing());

        Assert.False(vm.CopyFilteredCommand.CanExecute(null));
        Assert.False(vm.CopyEntryCommand.CanExecute(null));
        Assert.Equal("复制列表日志（0 条）", vm.CopyFilteredText);
    }

    [Fact]
    public void OptionsExposeShortDisplayNames()
    {
        var (vm, _, _, _, _, _) = Build(LogReadResult.Missing());

        Assert.All(vm.Sources, option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.DisplayName));
            Assert.DoesNotContain("LogSourceOption", option.DisplayName);
            Assert.DoesNotContain("{", option.DisplayName);
            Assert.Equal(option.DisplayName, option.ToString());
        });

        Assert.All(vm.LevelOptions, option =>
        {
            Assert.Equal(option.Label, option.DisplayName);
            Assert.Equal(option.Label, option.ToString());
        });

        Assert.All(vm.ScopeOptions, option =>
        {
            Assert.Equal(option.Label, option.DisplayName);
            Assert.Equal(option.Label, option.ToString());
        });

        Assert.Contains(vm.Sources, option => option.DisplayName == "BepInEx 日志");
        Assert.Contains(vm.LevelOptions, option => option.DisplayName == "全部");
        Assert.Contains(vm.ScopeOptions, option => option.DisplayName == "全部内容");
    }

    [Fact]
    public async Task CancellingFileSelectionKeepsTheCurrentSourceAndSnapshot()
    {
        var (vm, reader, _, _, _, _) = Build(Ok(Entry(LogLevel.Info, "keep")));

        await vm.OnActivatedAsync();
        var identity = vm.SelectedSource?.Source.Identity;
        var calls = reader.Calls;

        await vm.SelectLogFileCommand.ExecuteAsync(null);

        Assert.Equal(identity, vm.SelectedSource?.Source.Identity);
        Assert.Single(vm.Entries);
        Assert.Equal(calls, reader.Calls);
        Assert.DoesNotContain(vm.Sources, option => option.Source.Kind == SourceKind.ManualFile);
    }

    [Fact]
    public async Task FailedRefreshKeepsThePreviousSnapshot()
    {
        var (vm, reader, _, _, _, _) = Build(Ok(Entry(LogLevel.Info, "first")));

        await vm.OnActivatedAsync();
        reader.ByKind[SourceKind.BepInEx] = LogReadResult.Failed("denied");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Entries);
        Assert.Contains("刷新失败：denied", vm.StatusText);
        Assert.Contains("仍显示上次读取的内容", vm.StatusText);
    }

    [Fact]
    public async Task ClipboardFailureKeepsTheSnapshot()
    {
        var (vm, _, clipboard, _, shell, _) = Build(Ok(Entry(LogLevel.Info, "x")));
        clipboard.Result = false;

        await vm.OnActivatedAsync();
        await vm.CopyFilteredCommand.ExecuteAsync(null);

        Assert.Equal(StatusKind.Error, shell.LastKind);
        Assert.Single(vm.Entries);
    }

    [Fact]
    public async Task RefreshKeepsTheSelectedEntryByIdentity()
    {
        var entries = new[]
        {
            Entry(LogLevel.Info, "first", offset: 0),
            Entry(LogLevel.Error, "second", offset: 40)
        };
        var (vm, reader, _, _, _, _) = Build(Ok(entries));

        await vm.OnActivatedAsync();
        vm.SelectedEntry = vm.Entries[1];

        reader.ByKind[SourceKind.BepInEx] = Ok(entries);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedEntry);
        Assert.Equal("second", vm.SelectedEntry!.Raw);
    }

    [Fact]
    public async Task FilteringOutTheSelectionClearsIt()
    {
        var (vm, _, _, _, _, _) = Build(Ok(
            Entry(LogLevel.Info, "info", offset: 0),
            Entry(LogLevel.Error, "error", offset: 20)));

        await vm.OnActivatedAsync();
        vm.SelectedEntry = vm.Entries[0];

        vm.SelectedLevel = vm.LevelOptions.Single(option => option.Value == LogLevelFilter.Errors);

        Assert.Null(vm.SelectedEntry);
        Assert.False(vm.HasSelection);
        Assert.Empty(vm.DetailText);
    }

    [Fact]
    public async Task UnitySourceShowsTheAttributionText()
    {
        var (vm, _, _, _, _, _) = Build(LogReadResult.Missing());
        await vm.OnActivatedAsync();

        vm.SelectedSource = vm.Sources.Single(option => option.Source.Kind == SourceKind.UnityCurrent);

        Assert.Equal("显示该目录中已有的日志，不能确认生成时使用的实例。", vm.AttributionText);
    }

    [Fact]
    public async Task TruncationIsSurfacedAndDeclaredOnCopy()
    {
        var (vm, _, clipboard, _, _, _) = Build(LogReadResult.Ok([Entry(LogLevel.Info, "tail")], LogTruncation.Head));

        await vm.OnActivatedAsync();
        Assert.Contains("文件尾部", vm.TruncatedText);

        await vm.CopyFilteredCommand.ExecuteAsync(null);
        Assert.Contains("不是完整日志", clipboard.Text);
    }

    [Fact]
    public async Task OpeningAFileAddsAManualSource()
    {
        var reader = new StubReader();
        reader.ByKind[SourceKind.BepInEx] = Ok(Entry(LogLevel.Info, "bep"));
        var picker = new StubFilePicker { LogPath = @"C:\logs\custom.log" };
        var clipboard = new StubClipboard();
        var shell = new StubShell();
        var provider = new StubSourceProvider([BepInExSource(@"C:\game-a")]);

        var vm = new LogViewerViewModel(
            shell, provider, picker, clipboard, new StubLauncher(), reader)
        {
            SearchDebounce = TimeSpan.Zero
        };

        await vm.SelectLogFileCommand.ExecuteAsync(null);

        var manual = Assert.Single(vm.Sources, option => option.Source.Kind == SourceKind.ManualFile);
        Assert.Equal(manual.Source.Identity, vm.SelectedSource?.Source.Identity);
    }

    [Fact]
    public async Task ExternalOpenUsesTheCurrentPath()
    {
        var (vm, _, _, launcher, shell, _) = Build(Ok(Entry(LogLevel.Info, "x")));

        await vm.OnActivatedAsync();
        vm.OpenCurrentExternallyCommand.Execute(null);

        Assert.Equal(@"C:\game-a\BepInEx\LogOutput.log", launcher.FilePath);
        Assert.Equal(StatusKind.Success, shell.LastKind);
    }

    [Fact]
    public async Task ExternalOpenReportsAMissingTargetWithoutCreatingIt()
    {
        var (vm, _, _, launcher, shell, _) = Build(Ok(Entry(LogLevel.Info, "x")));
        launcher.FileResult = false;

        await vm.OnActivatedAsync();
        vm.OpenCurrentExternallyCommand.Execute(null);

        Assert.Equal(StatusKind.Warning, shell.LastKind);
        Assert.Contains("未打开", shell.LastStatus);
    }

    [Fact]
    public async Task OpenFolderUsesTheLogDirectory()
    {
        var (vm, _, _, launcher, _, _) = Build(Ok(Entry(LogLevel.Info, "x")));

        await vm.OnActivatedAsync();
        vm.OpenCurrentFolderCommand.Execute(null);

        Assert.Equal(@"C:\game-a\BepInEx", launcher.FolderPath);
    }

    [Fact]
    public async Task InstanceChangeReloadsTheBepInExSourceAndDropsOldRows()
    {
        var reader = new StubReader();
        reader.ByKind[SourceKind.BepInEx] = Ok(Entry(LogLevel.Info, "old"));
        var provider = new StubSourceProvider([BepInExSource(@"C:\game-a")]);
        var (vm, _, _, _, _, _) = BuildWith(provider, reader);

        await vm.OnActivatedAsync();
        Assert.Single(vm.Entries);

        reader.ByKind[SourceKind.BepInEx] = Ok(Entry(LogLevel.Info, "new"));
        provider.Items.Clear();
        provider.Items.Add(BepInExSource(@"C:\game-b"));

        await vm.OnInstanceChangedAsync();

        Assert.Equal("bepinex:C:\\game-b", vm.SelectedSource?.Source.Identity);
        Assert.Single(vm.Entries);
        Assert.Equal("new", vm.Entries[0].Raw);
        Assert.Equal(2, reader.Calls);
    }

    [Fact]
    public async Task InstanceChangeLeavesTheUnitySourceAlone()
    {
        var reader = new StubReader();
        reader.ByKind[SourceKind.UnityCurrent] = Ok(Entry(LogLevel.Info, "unity", "Unity Log"));
        var provider = new StubSourceProvider([UnitySource()]);
        var (vm, _, _, _, _, _) = BuildWith(provider, reader);

        await vm.OnActivatedAsync();
        var calls = reader.Calls;
        var identity = vm.SelectedSource?.Source.Identity;

        provider.Items.Insert(0, BepInExSource(@"C:\game-b"));
        await vm.OnInstanceChangedAsync();

        Assert.Equal(identity, vm.SelectedSource?.Source.Identity);
        Assert.Equal(calls, reader.Calls);
        Assert.Single(vm.Entries);
    }

    [Fact]
    public async Task RemovingTheInstanceShowsTheNoInstanceText()
    {
        var (vm, _, _, _, _, provider) = Build(Ok(Entry(LogLevel.Info, "x")));

        await vm.OnActivatedAsync();
        provider.Items.Clear();

        await vm.OnInstanceChangedAsync();

        Assert.Null(vm.SelectedSource);
        Assert.False(vm.HasEntries);
        Assert.Equal("请选择一个游戏实例，或切换到 Unity 日志 / 选择其他日志文件。", vm.StatusText);
    }

    private sealed class StubSourceProvider : ILogSourceProvider
    {
        public StubSourceProvider(IReadOnlyList<LogSource> sources) => Items = sources.ToList();

        public List<LogSource> Items { get; }

        public event EventHandler? SourcesChanged;

        public IReadOnlyList<LogSource> Describe() => Items.ToList();

        public void Raise() => SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class StubReader : ILogReader
    {
        public Dictionary<SourceKind, LogReadResult> ByKind { get; } = new();

        public int Calls { get; private set; }

        public Task<LogReadResult> ReadAsync(LogSource source, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(
                ByKind.TryGetValue(source.Kind, out var result)
                    ? result
                    : LogReadResult.Ok(Array.Empty<LogEntry>()));
        }
    }

    private sealed class StubClipboard : IClipboardService
    {
        public bool Result { get; set; } = true;

        public bool Throw { get; set; }

        public int Calls { get; private set; }

        public string? Text { get; private set; }

        public Task<bool> SetTextAsync(string text)
        {
            Calls++;
            Text = text;
            if (Throw) throw new InvalidOperationException("clipboard unavailable");
            return Task.FromResult(Result);
        }
    }

    private sealed class StubLauncher : IExternalLauncher
    {
        public bool FileResult { get; set; } = true;

        public string? FilePath { get; private set; }

        public bool FolderResult { get; set; } = true;

        public string? FolderPath { get; private set; }

        public bool TryOpenFile(string path, out string? error)
        {
            FilePath = path;
            error = FileResult ? null : "目标文件不存在，未打开。";
            return FileResult;
        }

        public bool TryOpenFolder(string path, out string? error)
        {
            FolderPath = path;
            error = FolderResult ? null : "目标文件夹不存在，未打开。";
            return FolderResult;
        }
    }

    private sealed class StubFilePicker : IFilePickerService
    {
        public string? LogPath { get; set; }

        public Task<string?> PickExecutableAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickArchiveAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickThemeFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickLogFileAsync() => Task.FromResult(LogPath);

        public Task<string?> PickDataFolderAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickBackupFolderAsync() => Task.FromResult<string?>(null);
    }

    private sealed class StubShell : IAppShell
    {
        public GameContext Game => null!;

        public IAppSettingsService Settings => null!;

        public StatusKind LastKind { get; private set; }

        public string LastStatus { get; private set; } = string.Empty;

        public void SetStatus(string message, StatusKind kind = StatusKind.Info)
        {
            LastStatus = message;
            LastKind = kind;
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
