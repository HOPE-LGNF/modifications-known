using System.Collections.ObjectModel;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Diagnostics.Logs;

namespace ModificationsKnown.App.ViewModels;

public sealed record LogSourceOption(LogSource Source, string Label, string Attribution)
{
    public string Identity => Source.Identity;

    public string PathText => Source.Path ?? string.Empty;

    public string DisplayName => Label;

    public string ToolTipText =>
        string.IsNullOrWhiteSpace(PathText) ? Label : $"{Label}\n{PathText}";

    public override string ToString() => Label;
}

public sealed record LogLevelOption(LogLevelFilter Value, string Label)
{
    public string DisplayName => Label;

    public override string ToString() => Label;
}

/// <summary>
/// The log page. Reads through <see cref="LogViewerCoordinator"/>; filtering is
/// pure and no log text ever reaches the app logger.
/// </summary>
public partial class LogViewerViewModel : ObservableObject, IAsyncPage
{
    private const string MissingText = "尚未找到日志。游戏运行后可能生成日志，也可以手动打开其他日志文件。";
    private const string EmptyText = "日志为空。";
    private const string NoSnapshotText = "尚未读取任何日志。";
    private const string NoMatchText = "没有匹配的日志。";
    private const string UnityAttribution = "显示该目录中已有的日志，不能确认生成时使用的实例。";
    private const string TruncatedNote = "只读取了最近 2 MiB，更早的内容没有载入。";

    private readonly IAppShell _shell;
    private readonly ILogSourceProvider _sourceProvider;
    private readonly LogViewerCoordinator _coordinator;

    private CancellationTokenSource? _cts;
    private bool _suppressReload;

    [ObservableProperty]
    private LogSourceOption? _selectedSource;

    [ObservableProperty]
    private LogLevelOption? _selectedLevel;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = NoSnapshotText;

    [ObservableProperty]
    private string _readAsOfText = string.Empty;

    [ObservableProperty]
    private string _attributionText = string.Empty;

    [ObservableProperty]
    private string _sourcePathText = string.Empty;

    [ObservableProperty]
    private string _truncatedText = string.Empty;

    [ObservableProperty]
    private string _emptyMessage = NoSnapshotText;

    [ObservableProperty]
    private string _filterSummary = string.Empty;

    [ObservableProperty]
    private bool _hasEntries;

    [ObservableProperty]
    private LogEntryRow? _selectedEntry;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _wrapDetail;

    public LogViewerViewModel(
        IAppShell shell,
        ILogSourceProvider sourceProvider,
        ILogReader reader)
    {
        _shell = shell;
        _sourceProvider = sourceProvider;
        _coordinator = new LogViewerCoordinator(reader);

        SelectedLevel = LevelOptions[0];
        RefreshSources();
    }

    public ObservableCollection<LogSourceOption> Sources { get; } = [];

    public ObservableCollection<LogEntryRow> Entries { get; } = [];

    public IReadOnlyList<LogLevelOption> LevelOptions { get; } =
    [
        new(LogLevelFilter.All, "全部"),
        new(LogLevelFilter.WarningsAndErrors, "警告与错误"),
        new(LogLevelFilter.Errors, "仅错误")
    ];

    public string Explanation =>
        "只读取本机日志文件的尾部内容，不上传、不修改、也不会把日志正文写进 MK 自己的日志。";

    public string DetailPlaceholder => HasSelection ? string.Empty : "选择一条日志查看详情。";

    public TextWrapping DetailWrapping => WrapDetail ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public ScrollBarVisibility DetailHorizontalScroll =>
        WrapDetail ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    public async Task OnActivatedAsync()
    {
        RefreshSources();
        await ReloadAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshCurrentSourceAsync();

    public async Task RefreshCurrentSourceAsync()
    {
        RefreshSources();
        await ReloadAsync();
    }

    partial void OnSelectedSourceChanged(LogSourceOption? value)
    {
        AttributionText = value?.Attribution ?? string.Empty;
        SourcePathText = value?.PathText ?? string.Empty;

        if (_suppressReload) return;
        _ = ReloadAsync();
    }

    partial void OnSelectedLevelChanged(LogLevelOption? value) => ApplyFilter();

    partial void OnSelectedEntryChanged(LogEntryRow? value)
    {
        DetailText = value?.Raw ?? string.Empty;
        HasSelection = value is not null;
        OnPropertyChanged(nameof(DetailPlaceholder));
    }

    partial void OnWrapDetailChanged(bool value)
    {
        OnPropertyChanged(nameof(DetailWrapping));
        OnPropertyChanged(nameof(DetailHorizontalScroll));
    }

    private void RefreshSources()
    {
        var previous = SelectedSource?.Identity;

        var options = _sourceProvider.Describe().Select(BuildOption).ToList();

        _suppressReload = true;
        Sources.Clear();
        foreach (var option in options) Sources.Add(option);
        SelectedSource = Sources.FirstOrDefault(option => option.Identity == previous)
                         ?? Sources.FirstOrDefault();
        _suppressReload = false;
    }

    private static LogSourceOption BuildOption(LogSource source) => source.Kind switch
    {
        SourceKind.BepInEx => new LogSourceOption(source, "BepInEx 日志", string.Empty),
        SourceKind.UnityCurrent => new LogSourceOption(source, "Unity 日志 · 当前游戏数据目录", UnityAttribution),
        SourceKind.UnityPrevious => new LogSourceOption(source, "Unity 日志 · 上一次运行", UnityAttribution),
        _ => new LogSourceOption(
            source,
            $"文件 · {Path.GetFileName(source.Path ?? "日志")}",
            string.Empty)
    };

    private async Task ReloadAsync()
    {
        if (SelectedSource is not { } option) return;

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        IsBusy = true;
        StatusText = "正在读取日志…";

        try
        {
            _coordinator.SetSource(option.Source);
            var result = await _coordinator.LoadAsync(cts.Token);
            if (cts.IsCancellationRequested) return;

            ApplyResult(result);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsBusy = false;
        }
    }

    private void ApplyResult(LogLoadResult result)
    {
        var snapshot = _coordinator.Snapshot;

        if (result.Adopted && snapshot is not null)
        {
            ReadAsOfText = $"读取于 {snapshot.ReadAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}";
            TruncatedText = snapshot.Truncated ? TruncatedNote : string.Empty;
            StatusText = snapshot.Outcome switch
            {
                LogReadOutcome.Ok => $"已读取 {snapshot.Entries.Count} 条日志。",
                LogReadOutcome.Missing => MissingText,
                LogReadOutcome.Empty => EmptyText,
                _ => EmptyText
            };

            ApplyFilter();
            return;
        }

        if (result.Outcome == LogReadOutcome.Cancelled)
        {
            StatusText = "已取消本次读取。";
            return;
        }

        if (result.KeptPreviousSnapshot && snapshot is not null)
        {
            StatusText = $"刷新失败：{result.Error}。仍显示上次读取的内容（{ReadAsOfText}）。";
            ApplyFilter();
            return;
        }

        StatusText = $"读取失败：{result.Error ?? "未知原因"}。";
        TruncatedText = string.Empty;
        FilterSummary = string.Empty;
        SelectedEntry = null;
        Entries.Clear();
        HasEntries = false;
        EmptyMessage = StatusText;
    }

    private void ApplyFilter()
    {
        var snapshot = _coordinator.Snapshot;
        var previous = SelectedEntry?.Entry;

        var rows = _coordinator
            .Filter(
                SelectedLevel?.Value ?? LogLevelFilter.All,
                LogSearchScope.All,
                null)
            .Select(entry => new LogEntryRow(entry))
            .ToList();

        Entries.Clear();
        foreach (var row in rows) Entries.Add(row);

        HasEntries = rows.Count > 0;
        FilterSummary = snapshot is null
            ? string.Empty
            : $"当前显示 {rows.Count} 条，共读取 {snapshot.Entries.Count} 条";

        // Keep the selection only when the same entry (identity, not index) is
        // still present; otherwise clear it instead of jumping to another row.
        SelectedEntry = previous is null
            ? null
            : rows.FirstOrDefault(row =>
                row.Entry.StartOffset == previous.StartOffset
                && string.Equals(row.Entry.Raw, previous.Raw, StringComparison.Ordinal));

        if (HasEntries)
        {
            EmptyMessage = string.Empty;
            return;
        }

        EmptyMessage = snapshot?.Outcome switch
        {
            LogReadOutcome.Missing => MissingText,
            LogReadOutcome.Empty => EmptyText,
            null => NoSnapshotText,
            _ => snapshot.Entries.Count == 0 ? EmptyText : NoMatchText
        };
    }
}
