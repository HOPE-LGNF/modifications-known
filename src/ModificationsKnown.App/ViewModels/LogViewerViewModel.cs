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
public partial class LogViewerViewModel : ObservableObject, IAsyncPage, IDisposable
{
    private const string MissingText = "尚未找到日志。游戏运行后可能生成日志，也可以手动打开其他日志文件。";
    private const string EmptyText = "日志为空。";
    private const string NoEntriesText = "没有可显示的完整日志条目。";
    private const string NoSnapshotText = "尚未读取任何日志。";
    private const string NoMatchText = "没有匹配的日志。";
    private const string NoInstanceText = "请选择一个游戏实例，或切换到 Unity 日志 / 选择其他日志文件。";
    private const string UnityAttribution = "显示该目录中已有的日志，不能确认生成时使用的实例。";
    private const string TruncatedNote = "只读取了文件尾部，更早的内容没有载入。";
    private const string DecodeNoteText = "有无法解码的字节，已用替换字符显示。";
    private const string TruncationNotice = "（本条目来自不完整的日志快照，不是完整日志。）";

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
    private Task? _loadTask;
    private bool _refreshing;
    private bool _disposed;

    /// <summary>Completes when the latest load triggered by a source change or
    /// activation has settled. Tests use it instead of polling.</summary>
    public Task WaitForLoadAsync() => _loadTask ?? Task.CompletedTask;

    /// <summary>Releases subscriptions and cancels work owned by the page.
    /// The shell disposes the page when the application exits.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sourceProvider.SourcesChanged -= OnSourcesChanged;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

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
    private string _decodeNote = string.Empty;

    [ObservableProperty]
    private string _emptyMessage = NoSnapshotText;

    [ObservableProperty]
    private string _filterSummary = string.Empty;

    [ObservableProperty]
    private string _copyHint =
        "「复制列表日志」复制左侧当前显示的全部条目（含未滚动到的）；「复制此条日志」只复制右侧选中的那一条。分享前检查日志中的敏感信息。";

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
        _sourceProvider.SourcesChanged += OnSourcesChanged;
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
        var previous = SelectedSource?.Identity;
        RefreshSources();

        var identity = SelectedSource?.Identity;
        var snapshot = _coordinator.Snapshot;

        // An adopted snapshot (including Missing/Empty) is not re-read just
        // because the user navigated back to the page.
        if (previous == identity
            && snapshot is not null
            && string.Equals(snapshot.Source.Identity, identity, StringComparison.Ordinal))
        {
            return;
        }

        _loadTask = ReloadAsync();
        await _loadTask;
    }

    /// <summary>Re-reads on demand, e.g. from the refresh button or an explicit
    /// caller. The refresh button ignores repeat clicks while one is running;
    /// a source switch still starts its own read.</summary>
    public async Task RefreshCurrentSourceAsync()
    {
        RefreshSources();
        _loadTask = ReloadAsync();
        await _loadTask;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAsync()
    {
        if (_refreshing) return;

        _refreshing = true;
        try
        {
            await RefreshCurrentSourceAsync();
        }
        finally
        {
            _refreshing = false;
        }
    }

    [RelayCommand]
    private async Task SelectLogFileAsync()
    {
        var path = await _filePicker.PickLogFileAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!LogSourceLocations.IsSupportedManualPath(path))
        {
            _shell.SetStatus("只能选择 .log 或 .txt 日志文件。", StatusKind.Warning);
            return;
        }

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
        _loadTask = ReloadAsync();
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

    private void OnSourcesChanged(object? sender, EventArgs e) => _ = OnInstanceChangedAsync();

    /// <summary>Reacts to an instance change: a BepInEx source is re-read at
    /// once and its old rows are dropped; Unity and manual sources keep their
    /// identity and content.</summary>
    public async Task OnInstanceChangedAsync()
    {
        var wasBepInEx = SelectedSource?.Source.Kind == SourceKind.BepInEx;
        RefreshSources();

        // Unity and manual sources do not follow the instance: their identity
        // stays the same, so RefreshSources keeps the selection untouched.
        if (!wasBepInEx) return;

        if (SelectedSource?.Source.Kind != SourceKind.BepInEx)
        {
            _cts?.Cancel();
            ClearSnapshotView(NoInstanceText);
            StatusText = NoInstanceText;
            return;
        }

        // A different instance means the old snapshot belongs to another game.
        _cts?.Cancel();
        ClearSnapshotView("正在读取日志…");
        StatusText = "实例已切换，正在读取新的日志…";
        _loadTask = ReloadAsync();
        await _loadTask;
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

        // First visit defaults to the instance's BepInEx log; later visits keep
        // whatever the user picked in this session (the view model survives
        // navigation, nothing is persisted across restarts).
        SelectedSource = Sources.FirstOrDefault(option => option.Identity == previous)
                         ?? Sources.FirstOrDefault(option => option.Source.Kind == SourceKind.BepInEx)
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

        // Cancel any in-flight read first: even when the new source has no path,
        // a late result from the previous source must not overwrite this state.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        // The BepInEx entry exists even without an instance, but it has no path:
        // show guidance instead of reading anything or falling back to Unity.
        if (string.IsNullOrWhiteSpace(option.Source.Path))
        {
            _coordinator.SetSource(option.Source);
            ClearSnapshotView(NoInstanceText);
            StatusText = NoInstanceText;
            IsBusy = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;

        var changed = _coordinator.SetSource(option.Source);
        if (changed)
        {
            // A different source: the old rows, timestamp and notes must be
            // gone before the new read starts, so nothing can be copied from
            // the previous file.
            ClearSnapshotView("正在读取日志…");
            StatusText = "正在读取日志…";
        }
        else if (_coordinator.Snapshot is not null)
        {
            StatusText = "正在刷新，显示上次结果…";
        }
        else
        {
            StatusText = "正在读取日志…";
        }

        IsBusy = true;

        try
        {
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

    private void ClearSnapshotView(string emptyMessage)
    {
        Entries.Clear();
        SelectedEntry = null;
        HasEntries = false;
        FilterSummary = string.Empty;
        CopyFilteredText = "复制列表日志（0 条）";
        ReadAsOfText = string.Empty;
        TruncatedText = string.Empty;
        DecodeNote = string.Empty;
        EmptyMessage = emptyMessage;
    }

    private void ApplyResult(LogLoadResult result)
    {
        var snapshot = _coordinator.Snapshot;

        if (result.Adopted && snapshot is not null)
        {
            ReadAsOfText = $"读取于 {snapshot.ReadAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}";
            TruncatedText = DescribeTruncation(snapshot.Truncation);
            DecodeNote = snapshot.HadDecodeFallback ? DecodeNoteText : string.Empty;
            StatusText = snapshot.Outcome switch
            {
                LogReadOutcome.Ok => snapshot.Entries.Count == 0
                    ? NoEntriesText
                    : $"已读取 {snapshot.Entries.Count} 条日志。",
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

        if (result.ErrorKind == LogLoadError.SourceNotSelected)
        {
            StatusText = NoInstanceText;
            ClearSnapshotView(NoInstanceText);
            return;
        }

        if (result.KeptPreviousSnapshot && snapshot is not null)
        {
            StatusText = $"刷新失败：{result.Error}。仍显示上次读取的内容（{ReadAsOfText}）。";
            ApplyFilter();
            return;
        }

        StatusText = $"读取失败：{result.Error ?? "未知原因"}。";
        ClearSnapshotView(StatusText);
    }

    private static string DescribeTruncation(LogTruncation truncation)
    {
        if (truncation == LogTruncation.None) return string.Empty;

        var parts = new List<string>();
        if (truncation.HasFlag(LogTruncation.Head))
        {
            parts.Add(TruncatedNote.TrimEnd('。'));
        }

        if (truncation.HasFlag(LogTruncation.EntryCount))
        {
            parts.Add("只保留最近的一部分条目，更早的条目没有载入");
        }

        if (truncation.HasFlag(LogTruncation.EntryLength))
        {
            parts.Add("有单条日志过长，只保留了尾部内容");
        }

        return string.Join("；", parts) + "。";
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
            LogReadOutcome.Ok => snapshot.Entries.Count == 0 ? NoEntriesText : NoMatchText,
            null => NoSnapshotText,
            _ => snapshot.Entries.Count == 0 ? NoEntriesText : NoMatchText
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

        bool copied;
        try
        {
            copied = await _clipboard.SetTextAsync(builder.ToString());
        }
        catch (Exception)
        {
            // Platform clipboard failures must not escape the command; the log
            // text itself never reaches the app logger.
            copied = false;
        }

        if (copied)
        {
            _shell.SetStatus($"已复制 {rows.Count} 条日志到剪贴板。", StatusKind.Success);
        }
        else
        {
            _shell.SetStatus("复制失败：剪贴板不可用。", StatusKind.Error);
        }
    }
}
