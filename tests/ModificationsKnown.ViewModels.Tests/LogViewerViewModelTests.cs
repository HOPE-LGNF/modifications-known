using ModificationsKnown.App.Services;
using ModificationsKnown.App.ViewModels;
using ModificationsKnown.Core.Diagnostics.Logs;
using ModificationsKnown.Core.Games;
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
        LogReadResult.Ok(entries, false);

    private static (LogViewerViewModel Vm, StubReader Reader, StubShell Shell, StubSourceProvider Provider)
        Build(LogReadResult bepInExResult, params LogSource[] sources)
    {
        var reader = new StubReader();
        reader.ByKind[SourceKind.BepInEx] = bepInExResult;
        var provider = new StubSourceProvider(sources.Length == 0
            ? [BepInExSource(@"C:\game-a"), UnitySource()]
            : sources);

        return BuildWith(provider, reader);
    }

    private static (LogViewerViewModel Vm, StubReader Reader, StubShell Shell, StubSourceProvider Provider)
        BuildWith(StubSourceProvider provider, StubReader reader)
    {
        var shell = new StubShell();
        var vm = new LogViewerViewModel(shell, provider, reader);

        return (vm, reader, shell, provider);
    }

    [Fact]
    public async Task ActivationLoadsTheFirstSource()
    {
        var (vm, reader, _, _) = Build(Ok(Entry(LogLevel.Info, "hello")));

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
        var (vm, _, _, _) = Build(LogReadResult.Missing());

        await vm.OnActivatedAsync();

        Assert.False(vm.HasEntries);
        Assert.Equal("尚未找到日志。游戏运行后可能生成日志，也可以手动打开其他日志文件。", vm.StatusText);
        Assert.Equal(vm.StatusText, vm.EmptyMessage);
    }

    [Fact]
    public async Task EmptyFileIsNotAnError()
    {
        var (vm, _, _, _) = Build(LogReadResult.Empty());

        await vm.OnActivatedAsync();

        Assert.Equal("日志为空。", vm.StatusText);
        Assert.False(vm.HasEntries);
    }

    [Fact]
    public async Task LevelFilterNarrowsTheRows()
    {
        var (vm, _, _, _) = Build(Ok(
            Entry(LogLevel.Info, "loading"),
            Entry(LogLevel.Warning, "warn"),
            Entry(LogLevel.Error, "boom")));

        await vm.OnActivatedAsync();
        Assert.Equal(3, vm.Entries.Count);

        vm.SelectedLevel = vm.LevelOptions.Single(option => option.Value == LogLevelFilter.Errors);

        Assert.Single(vm.Entries);
        Assert.Equal("ERROR", vm.Entries[0].LevelText);
        Assert.Equal("当前显示 1 条，共读取 3 条", vm.FilterSummary);
    }

    [Fact]
    public async Task SelectingARowFillsTheDetailPane()
    {
        var (vm, _, _, _) = Build(Ok(Entry(LogLevel.Error, "boom\n  at Foo.Bar")));

        await vm.OnActivatedAsync();
        Assert.False(vm.HasSelection);

        vm.SelectedEntry = vm.Entries[0];

        Assert.True(vm.HasSelection);
        Assert.Contains("at Foo.Bar", vm.DetailText);
        Assert.Equal("boom", vm.Entries[0].FirstLine);
    }

    [Fact]
    public void WrapDetailSwitchesWrappingAndDisablesHorizontalScroll()
    {
        var (vm, _, _, _) = Build(LogReadResult.Missing());

        Assert.False(vm.WrapDetail);
        Assert.Equal(Avalonia.Media.TextWrapping.NoWrap, vm.DetailWrapping);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, vm.DetailHorizontalScroll);

        vm.WrapDetail = true;

        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, vm.DetailWrapping);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, vm.DetailHorizontalScroll);
    }

    [Fact]
    public void OptionsExposeShortDisplayNames()
    {
        var (vm, _, _, _) = Build(LogReadResult.Missing());

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

        Assert.Contains(vm.Sources, option => option.DisplayName == "BepInEx 日志");
        Assert.Contains(vm.LevelOptions, option => option.DisplayName == "全部");
    }

    [Fact]
    public async Task FailedRefreshKeepsThePreviousSnapshot()
    {
        var (vm, reader, _, _) = Build(Ok(Entry(LogLevel.Info, "first")));

        await vm.OnActivatedAsync();
        reader.ByKind[SourceKind.BepInEx] = LogReadResult.Failed("denied");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Entries);
        Assert.Contains("刷新失败：denied", vm.StatusText);
        Assert.Contains("仍显示上次读取的内容", vm.StatusText);
    }

    [Fact]
    public async Task RefreshKeepsTheSelectedEntryByIdentity()
    {
        var entries = new[]
        {
            Entry(LogLevel.Info, "first", offset: 0),
            Entry(LogLevel.Error, "second", offset: 40)
        };
        var (vm, reader, _, _) = Build(Ok(entries));

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
        var (vm, _, _, _) = Build(Ok(
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
        var (vm, _, _, _) = Build(LogReadResult.Missing());
        await vm.OnActivatedAsync();

        vm.SelectedSource = vm.Sources.Single(option => option.Source.Kind == SourceKind.UnityCurrent);

        Assert.Equal("显示该目录中已有的日志，不能确认生成时使用的实例。", vm.AttributionText);
    }

    [Fact]
    public async Task TruncationIsSurfaced()
    {
        var (vm, _, _, _) = Build(LogReadResult.Ok([Entry(LogLevel.Info, "tail")], true));

        await vm.OnActivatedAsync();

        Assert.Contains("2 MiB", vm.TruncatedText);
    }

    private sealed class StubSourceProvider : ILogSourceProvider
    {
        private readonly IReadOnlyList<LogSource> _sources;

        public StubSourceProvider(IReadOnlyList<LogSource> sources) => _sources = sources;

        public IReadOnlyList<LogSource> Describe() => _sources;
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
                    : LogReadResult.Ok(Array.Empty<LogEntry>(), false));
        }
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
