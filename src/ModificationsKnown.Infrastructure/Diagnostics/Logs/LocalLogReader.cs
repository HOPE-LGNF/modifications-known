using System.Text;
using ModificationsKnown.Core.Diagnostics.Logs;

namespace ModificationsKnown.Infrastructure.Diagnostics.Logs;

public sealed record LocalLogReaderOptions(
    int MaxBytes = 2 * 1024 * 1024,
    int MaxEntries = 5000,
    int MaxEntryChars = 32 * 1024);

/// <summary>
/// Reads the tail of a log file through a single handle: one open per call
/// covers encoding probe, length, seek and read. Never writes, never locks the
/// game out.
/// </summary>
public sealed class LocalLogReader : ILogReader
{
    private const int ProbeBytes = 4;
    private const int Utf8LookBackBytes = 3;
    private const int Utf8CodePage = 65001;
    private const int Utf16LeCodePage = 1200;
    private const int Utf16BeCodePage = 1201;

    /// <summary>Fixed extra bytes a read may consume beyond the window budget
    /// (encoding probe plus the UTF-8 look-back used for alignment).</summary>
    public const int ReadOverheadBytes = ProbeBytes + Utf8LookBackBytes;

    private readonly LocalLogReaderOptions _options;

    /// <summary>Test seam: runs after the encoding probe and before the tail
    /// read, so tests can cancel or rotate the file at a deterministic point.</summary>
    internal Action? AfterProbeHook { get; set; }

    public LocalLogReader(LocalLogReaderOptions? options = null) =>
        _options = options ?? new LocalLogReaderOptions();

    public async Task<LogReadResult> ReadAsync(LogSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source.Path))
        {
            return LogReadResult.Failed("日志来源没有可读取的路径。");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return LogReadResult.Cancelled();
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                source.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return LogReadResult.Missing();
        }
        catch (Exception exception) when (exception is IOException
                                                    or UnauthorizedAccessException
                                                    or NotSupportedException
                                                    or ArgumentException)
        {
            return LogReadResult.Failed(exception.Message);
        }

        await using (stream)
        {
            return await ReadFromStreamAsync(stream, source, cancellationToken);
        }
    }

    private async Task<LogReadResult> ReadFromStreamAsync(
        FileStream stream,
        LogSource source,
        CancellationToken cancellationToken)
    {
        long bytesRead = 0;

        long length;
        try
        {
            length = stream.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return LogReadResult.Failed(exception.Message);
        }

        if (length == 0)
        {
            return LogReadResult.Empty();
        }

        try
        {
            var head = await ReadAtAsync(stream, 0, (int)Math.Min(ProbeBytes, length), cancellationToken);
            bytesRead += head.Length;
            var (codePage, bomLength) = DetectEncoding(head);

            AfterProbeHook?.Invoke();

            var windowStart = Math.Max(bomLength, length - _options.MaxBytes);
            var windowLength = (int)Math.Min(length - windowStart, _options.MaxBytes);
            if (codePage == Utf8CodePage && windowStart > bomLength)
            {
                // Look back a few bytes so a multi-byte UTF-8 character that
                // straddles the window edge is skipped instead of replaced.
                windowStart = Math.Max(bomLength, windowStart - Utf8LookBackBytes);
                windowLength = (int)Math.Min(length - windowStart, _options.MaxBytes + Utf8LookBackBytes);
            }
            else if (codePage is Utf16LeCodePage or Utf16BeCodePage && windowStart > bomLength)
            {
                // UTF-16 code units are two bytes: align down to an even offset
                // relative to the content start so the window never splits one.
                windowStart = bomLength + ((windowStart - bomLength) & ~1L);
                windowLength = (int)Math.Min(length - windowStart, _options.MaxBytes + 1);
            }

            var buffer = await ReadAtAsync(stream, windowStart, windowLength, cancellationToken);
            bytesRead += buffer.Length;

            var tracker = new DecoderFallbackTracker();
            var encoding = Encoding.GetEncoding(
                codePage,
                EncoderFallback.ReplacementFallback,
                tracker);

            var skip = AlignWindow(buffer, bomLength, windowStart, codePage);
            var text = encoding.GetString(buffer, skip, buffer.Length - skip);
            var lineMap = BuildLineMap(buffer, skip, codePage, windowStart, encoding);

            var headCut = windowStart > bomLength;
            var droppedChars = 0;
            if (headCut)
            {
                // The window may begin mid-line; drop the fragment so parsing
                // never invents an entry from a partial prefix. Entry offsets
                // stay relative to the trimmed text and are shifted back when
                // they are mapped to original bytes.
                var newline = text.IndexOf('\n');
                if (newline >= 0)
                {
                    droppedChars = newline + 1;
                    text = text[droppedChars..];
                }
                else
                {
                    text = string.Empty;
                }
            }

            var parsed = LogParsers.For(source.Kind).Parse(text, headTruncated: headCut);
            var entries = MapAbsoluteOffsets(parsed, lineMap, droppedChars);

            var truncation = headCut ? LogTruncation.Head : LogTruncation.None;

            if (entries.Count > _options.MaxEntries)
            {
                entries = entries.Skip(entries.Count - _options.MaxEntries).ToList();
                truncation |= LogTruncation.EntryCount;
            }

            var capped = new List<LogEntry>(entries.Count);
            foreach (var entry in entries)
            {
                var cappedEntry = Cap(entry);
                if (!ReferenceEquals(cappedEntry, entry))
                {
                    truncation |= LogTruncation.EntryLength;
                }

                capped.Add(cappedEntry);
            }

            return LogReadResult.Ok(capped, truncation, tracker.Occurred, bytesRead);
        }
        catch (OperationCanceledException)
        {
            return LogReadResult.Cancelled(bytesRead);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return LogReadResult.Failed(exception.Message, bytesRead);
        }
    }

    private static int AlignWindow(byte[] buffer, int bomLength, long windowStart, int codePage)
    {
        if (codePage == Utf8CodePage && windowStart > bomLength)
        {
            var skip = 0;
            while (skip < buffer.Length && (buffer[skip] & 0xC0) == 0x80)
            {
                skip++;
            }

            return skip;
        }

        return 0;
    }

    private static List<LogEntry> MapAbsoluteOffsets(
        IReadOnlyList<LogEntry> entries,
        IReadOnlyList<(int CharOffset, long ByteOffset)> lineMap,
        int droppedChars)
    {
        var mapped = new List<LogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var target = (int)Math.Clamp(entry.StartOffset, 0, int.MaxValue) + droppedChars;
            mapped.Add(entry with { StartOffset = ResolveByteOffset(lineMap, target) });
        }

        return mapped;
    }

    private static long ResolveByteOffset(
        IReadOnlyList<(int CharOffset, long ByteOffset)> lineMap,
        int charOffset)
    {
        var low = 0;
        var high = lineMap.Count - 1;
        var result = 0L;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (lineMap[mid].CharOffset <= charOffset)
            {
                result = lineMap[mid].ByteOffset;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    /// <summary>Maps each line start's character index to its byte offset in the
    /// file. Built from the raw bytes (not by re-encoding the decoded text), so
    /// replaced bytes cannot shift later offsets.</summary>
    private static List<(int CharOffset, long ByteOffset)> BuildLineMap(
        byte[] buffer,
        int skip,
        int codePage,
        long windowStart,
        Encoding encoding)
    {
        var map = new List<(int CharOffset, long ByteOffset)>();
        var charCursor = 0;
        var position = skip;

        if (codePage == Utf8CodePage)
        {
            while (position <= buffer.Length)
            {
                map.Add((charCursor, windowStart + position));
                if (position == buffer.Length) break;

                var newline = Array.IndexOf(buffer, (byte)0x0A, position);
                if (newline < 0) break;

                charCursor += encoding.GetString(buffer, position, newline - position).Length + 1;
                position = newline + 1;
            }

            return map;
        }

        while (position + 1 < buffer.Length)
        {
            map.Add((charCursor, windowStart + position));

            var index = position;
            while (index + 1 < buffer.Length)
            {
                var isLineFeed = codePage == Utf16LeCodePage
                    ? buffer[index] == 0x0A && buffer[index + 1] == 0
                    : buffer[index] == 0 && buffer[index + 1] == 0x0A;

                index += 2;
                if (isLineFeed) break;
            }

            charCursor += (index - position) / 2;
            position = index;
        }

        return map;
    }

    private LogEntry Cap(LogEntry entry)
    {
        if (entry.Message.Length <= _options.MaxEntryChars && entry.Raw.Length <= _options.MaxEntryChars)
        {
            return entry;
        }

        return entry with
        {
            Message = Tail(entry.Message),
            Raw = Tail(entry.Raw)
        };
    }

    private string Tail(string text) =>
        text.Length <= _options.MaxEntryChars ? text : text[^_options.MaxEntryChars..];

    private static async Task<byte[]> ReadAtAsync(
        FileStream stream,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        if (read < count)
        {
            Array.Resize(ref buffer, read);
        }

        return buffer;
    }

    private static (int CodePage, int BomLength) DetectEncoding(byte[] head)
    {
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return (Utf8CodePage, 3);
        }

        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            return (Utf16LeCodePage, 2);
        }

        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            return (Utf16BeCodePage, 2);
        }

        return (Utf8CodePage, 0);
    }

    /// <summary>Records whether the decoder had to replace invalid bytes. A
    /// literal U+FFFD in a valid file must not set this flag.</summary>
    private sealed class DecoderFallbackTracker : DecoderFallback
    {
        public bool Occurred { get; private set; }

        public override int MaxCharCount => 1;

        public override DecoderFallbackBuffer CreateFallbackBuffer() => new Buffer(this);

        private sealed class Buffer : DecoderFallbackBuffer
        {
            private readonly DecoderFallbackTracker _owner;
            private int _pending;

            public Buffer(DecoderFallbackTracker owner) => _owner = owner;

            public override int Remaining => _pending;

            public override bool Fallback(byte[] bytesUnknown, int index)
            {
                _owner.Occurred = true;
                _pending = 1;
                return true;
            }

            public override char GetNextChar()
            {
                if (_pending == 0) return '\0';

                _pending--;
                return '\uFFFD';
            }

            public override bool MovePrevious() => false;
        }
    }
}

/// <summary>Builds the sources the page offers. No scanning: every path is
/// derived from the selected instance or the documented LocalLow location.</summary>
public static class LogSourceLocations
{
    private const string UnityCompany = "Orsoniks";
    private const string UnityProduct = "CasualtiesUnknown";

    public static LogSource BepInEx(string instanceDirectory, string instanceKey) =>
        new(
            SourceKind.BepInEx,
            $"bepinex:{instanceKey}",
            Path.Combine(instanceDirectory, "BepInEx", "LogOutput.log"),
            instanceKey);

    public static LogSource UnityCurrent(string? localLowRoot = null) =>
        Unity(SourceKind.UnityCurrent, "unity:current:", "Player.log", localLowRoot);

    public static LogSource UnityPrevious(string? localLowRoot = null) =>
        Unity(SourceKind.UnityPrevious, "unity:previous:", "Player-prev.log", localLowRoot);

    public static LogSource Manual(string path)
    {
        var full = Path.GetFullPath(path);
        return new LogSource(SourceKind.ManualFile, "manual:" + full, full);
    }

    /// <summary>The page only accepts plain-text log files. The picker filter
    /// is not a guarantee: a path can arrive from elsewhere, so the same rule
    /// is enforced before a source is created.</summary>
    public static bool IsSupportedManualPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && (path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

    private static LogSource Unity(SourceKind kind, string identityPrefix, string fileName, string? root)
    {
        var localLow = root ?? DefaultLocalLowRoot();
        var path = Path.Combine(localLow, UnityCompany, UnityProduct, fileName);
        return new LogSource(kind, identityPrefix + path, path);
    }

    private static string DefaultLocalLowRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(Path.GetDirectoryName(local.TrimEnd(Path.DirectorySeparatorChar))!, "LocalLow");
    }
}
