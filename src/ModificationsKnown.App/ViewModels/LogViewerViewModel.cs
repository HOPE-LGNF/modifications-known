using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Diagnostics.Logs;
using ModificationsKnown.Infrastructure.Diagnostics.Logs;

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

public sealed record LogSearchScopeOption(LogSearchScope Value, string Label)
{
    public string DisplayName => Label;

    public override string ToString() => Label;
}

/// <summary>
/// The log page. Reads through <see cref="LogViewerCoordinator"/>; filtering is
/// pure, copying needs no dialog, and no log text ever reaches the app logger.
/// </summary>
public partial class LogViewerViewModel : ObservableObject, IAsyncPage
{
    private const string MissingText = "尚未找到日志。游戏运行后可能生成日志，也可以手动打开其他日志文件。";
    private const string EmptyText = "日志为空。";
    private const string NoSnapshotText = "尚未读取任何日志。";
    private const string NoMatchText = "没有匹配的日志。";
    private const string UnityAttribution = "显示该目录中已有的日志，不能确认生成时使用的实例。";
    private const string TruncatedNote = "只读取了最近 2 MiB，更早的内容没有载入。";
    private const string TruncationNotice = "（本条目来自截断后的日志尾部快照，不是完整日志。）";

    private readonly IAppShell _shell;
    private readonly ILogSourceProvider _sourceProvider;
    private readonly IFilePickerService _filePicker;
    private readonly IClipboardService _clipboard;
    private readonly IExternalLauncher _externalLauncher;
    private readonly LogViewerCoordinator _coordinator;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _searchCts;
    private LogSource? _manualSource;
    private bool _suppressReload;

    [ObservableProperty]
    private LogSourceOption? _selectedSource;

    [ObservableProperty]
    private LogLevelOption? _selectedLevel;

    [ObservableProperty]
    private LogSearchScopeOption? _selectedScope;

    [ObservableProperty]
    private string _searchText = string.Empty;

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
    private string _copyHint =
        "「复制列表日志」复制左侧当前显示的全部条目（含未滚动到的）；「复制此条日志」只复制右侧选中的那一条。";

    [ObservableProperty]
    private string _copyFilteredText = "复制列表日志（0 条）";

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
        IFilePickerService filePicker,
        IClipboardService clipboard,
        IExternalLauncher externalLauncher,
        ILogReader reader)
    {
        _shell = shell;
        _sourceProvider = sourceProvider;
        _filePicker = filePicker;
        _clipboard = clipboard;
        _externalLauncher = externalLauncher;
        _coordinator = new LogViewerCoordinator(reader);

        SelectedLevel = LevelOptions[0];
        SelectedScope = ScopeOptions[0];
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

    public IReadOnlyList<LogSearchScopeOption> ScopeOptions { get; } =
    [
        new(LogSearchScope.All, "全部内容"),
        new(LogSearchScope.Source, "日志来源"),
        new(LogSearchScope.Message, "日志内容")
    ];

    /// <summary>Delay before applying a search. Tests set it to zero.</summary>
    public TimeSpan SearchDebounce { get; set; } = TimeSpan.FromMilliseconds(200);

    public string Explanation =>
        "只读取本机日志文件的尾部内容，不上传、不修改、也不会把日志正文写进 MK 自己的日志；复制只会写入本机剪贴板。";

    public string DetailPlaceholder => HasSelection ? string.Empty : "选择一条日志查看详情。";

    public string SearchWatermark => SelectedScope?.Value switch
    {
        LogSearchScope.Source => "搜索日志来源，例如 BepInEx、Together",
        LogSearchScope.Message => "搜索日志内容（字面匹配，不区分大小写）",
        _ => "搜索日志来源或日志内容（字面匹配，不区分大小写）"
    };

    public TextWrapping DetailWrapping => WrapDetail ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public ScrollBarVisibility DetailHorizontalScroll =>
        WrapDetail ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    public async Task OnActivatedAsync()
    {
        RefreshSources();
        await ReloadAsync();
    }

    /// <summary>Re-reads on demand. Used by the refresh button and, for a
    /// BepInEx source only, when the selected instance changes.</summary>
    public async Task RefreshCurrentSourceAsync()
    {
        RefreshSources();
        await ReloadAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshCurrentSourceAsync();

    [RelayCommand]
    private async Task SelectLogFileAsync()
    {
        var path = await _filePicker.PickLogFileAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        _manualSource = LogSourceLocations.Manual(path);
        RefreshSources();

        _suppressReload = true;
        SelectedSource = Sources.FirstOrDefault(option => option.Identity == _manualSource.Identity);
        _suppressReload = false;

        await ReloadAsync();
    }

    [RelayCommand]
    private void OpenCurrentExternally()
    {
        if (SelectedSource?.Source.Path is not { Length: > 0 } path)
        {
            _shell.SetStatus("当前没有可打开的日志。", StatusKind.Warning);
            return;
        }

        if (_externalLauncher.TryOpenFile(path, out var error))
        {
            _shell.SetStatus("已用默认程序打开当前日志。", StatusKind.Success);
            return;
        }

        _shell.SetStatus($"未打开：{error}", StatusKind.Warning);
    }

    [RelayCommand]
    private void OpenCurrentFolder()
    {
        if (SelectedSource?.Source.Path is not { Length: > 0 } path)
        {
            _shell.SetStatus("当前没有可定位的日志。", StatusKind.Warning);
            return;
        }

        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder))
        {
            _shell.SetStatus("当前日志没有所在文件夹。", StatusKind.Warning);
            return;
        }

        if (_externalLauncher.TryOpenFolder(folder, out var error))
        {
            _shell.SetStatus("已打开日志所在文件夹。", StatusKind.Success);
            return;
        }

        _shell.SetStatus($"未打开：{error}", StatusKind.Warning);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task CopyEntryAsync() =>
        CopyAsync(SelectedEntry is { } row ? [row] : []);

    [RelayCommand(CanExecute = nameof(HasEntries))]
    private Task CopyFilteredAsync() =>
        CopyAsync(Entries.ToList());

    partial void OnSelectedSourceChanged(LogSourceOption? value)
    {
        AttributionText = value?.Attribution ?? string.Empty;
        SourcePathText = value?.PathText ?? string.Empty;

        if (_suppressReload) return;
        _ = ReloadAsync();
    }

    partial void OnSelectedScopeChanged(LogSearchScopeOption? value)
    {
        OnPropertyChanged(nameof(SearchWatermark));
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (SearchDebounce <= TimeSpan.Zero)
        {
            ApplyFilter();
            return;
        }

        _ = DebounceSearchAsync();
    }

    partial void OnSelectedLevelChanged(LogLevelOption? value) => ApplyFilter();

    partial void OnSelectedEntryChanged(LogEntryRow? value)
    {
        DetailText = value?.Raw ?? string.Empty;
        HasSelection = value is not null;
        OnPropertyChanged(nameof(DetailPlaceholder));
        CopyEntryCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasEntriesChanged(bool value) =>
        CopyFilteredCommand.NotifyCanExecuteChanged();

    partial void OnWrapDetailChanged(bool value)
    {
        OnPropertyChanged(nameof(DetailWrapping));
        OnPropertyChanged(nameof(DetailHorizontalScroll));
    }

    private async Task DebounceSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            await Task.Delay(SearchDebounce, cts.Token);
            if (cts.IsCancellationRequested) return;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RefreshSources()
    {
        var previous = SelectedSource?.Identity;

        var options = _sourceProvider.Describe().Select(BuildOption).ToList();
        if (_manualSource is not null && options.All(option => option.Identity != _manualSource.Identity))
        {
            options.Add(BuildOption(_manualSource));
        }

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
        CopyFilteredText = "复制列表日志（0 条）";
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
                SelectedScope?.Value ?? LogSearchScope.All,
                SearchText)
            .Select(entry => new LogEntryRow(entry))
            .ToList();

        Entries.Clear();
        foreach (var row in rows) Entries.Add(row);

        HasEntries = rows.Count > 0;
        CopyFilteredText = $"复制列表日志（{rows.Count} 条）";
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

    private async Task CopyAsync(IReadOnlyList<LogEntryRow> rows)
    {
        if (rows.Count == 0)
        {
            _shell.SetStatus("当前没有可复制的内容。", StatusKind.Warning);
            return;
        }

        var builder = new StringBuilder();
        if (SelectedSource is { } option)
        {
            builder.Append("== ").Append(option.Label);
            if (ReadAsOfText.Length > 0) builder.Append(" · ").Append(ReadAsOfText);
            builder.AppendLine(" ==");
        }

        if (_coordinator.Snapshot?.Truncated == true)
        {
            builder.AppendLine(TruncationNotice);
        }

        foreach (var row in rows)
        {
            builder.AppendLine(row.Raw);
        }

        if (await _clipboard.SetTextAsync(builder.ToString()))
        {
            _shell.SetStatus($"已复制 {rows.Count} 条日志到剪贴板。", StatusKind.Success);
        }
        else
        {
            _shell.SetStatus("复制失败：剪贴板不可用。", StatusKind.Error);
        }
    }
}
