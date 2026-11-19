namespace ModificationsKnown.Core.Diagnostics.Logs;

/// <summary>
/// Owns the log page state: which source is selected, the last adopted
/// snapshot, a generation counter that discards late reads, and pure filtering.
/// No disk access, no UI text, no clipboard.
/// </summary>
public sealed class LogViewerCoordinator
{
    private readonly ILogReader _reader;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private long _generation;
    private LogSource? _source;
    private LogSnapshot? _snapshot;

    public LogViewerCoordinator(ILogReader reader, Func<DateTimeOffset>? clock = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public LogSource? Source
    {
        get
        {
            lock (_gate) return _source;
        }
    }

    public LogSnapshot? Snapshot
    {
        get
        {
            lock (_gate) return _snapshot;
        }
    }

    public long Generation
    {
        get
        {
            lock (_gate) return _generation;
        }
    }

    /// <summary>Selects a source. A different identity invalidates the snapshot
    /// and any in-flight read. Returns true when the source actually changed.</summary>
    public bool SetSource(LogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (_source is not null
                && string.Equals(_source.Identity, source.Identity, StringComparison.Ordinal))
            {
                _source = source;
                return false;
            }

            _source = source;
            _snapshot = null;
            _generation++;
            return true;
        }
    }

    /// <summary>Reads the current source once. Late reads whose identity or
    /// generation no longer match are discarded instead of adopted.</summary>
    public async Task<LogLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        LogSource source;
        long generation;

        lock (_gate)
        {
            if (_source is null)
            {
                return new LogLoadResult(
                    false,
                    LogReadOutcome.Failed,
                    null,
                    false,
                    LogLoadError.SourceNotSelected);
            }

            source = _source;
            generation = ++_generation;
        }

        var result = await _reader.ReadAsync(source, cancellationToken);

        lock (_gate)
        {
            if (generation != _generation
                || _source is null
                || !string.Equals(_source.Identity, source.Identity, StringComparison.Ordinal))
            {
                return new LogLoadResult(false, result.Outcome, result.Error, false);
            }

            if (result.Outcome is LogReadOutcome.Failed or LogReadOutcome.Cancelled)
            {
                return new LogLoadResult(
                    false,
                    result.Outcome,
                    result.Error,
                    _snapshot is not null);
            }

            _snapshot = new LogSnapshot(
                source,
                result.Entries,
                result.Outcome,
                result.Truncation,
                result.HadDecodeFallback,
                result.BytesRead,
                _clock(),
                result.Error);

            return new LogLoadResult(true, result.Outcome, result.Error, false);
        }
    }

    /// <summary>Filters the current snapshot; returns an empty list when there
    /// is no snapshot.</summary>
    public IReadOnlyList<LogEntry> Filter(LogLevelFilter level, LogSearchScope scope, string? search)
    {
        LogSnapshot? snapshot;
        lock (_gate) snapshot = _snapshot;

        return snapshot is null
            ? Array.Empty<LogEntry>()
            : LogFilter.Apply(snapshot.Entries, level, scope, search);
    }
}
