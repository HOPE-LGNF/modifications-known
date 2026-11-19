using System.Text;
using ModificationsKnown.Core.Diagnostics.Logs;

namespace ModificationsKnown.Infrastructure.Diagnostics.Logs;

public sealed record LocalLogReaderOptions(
    int MaxBytes = 2 * 1024 * 1024,
    int MaxEntries = 5000,
    int MaxEntryChars = 32 * 1024);

/// <summary>
/// Reads the tail of a log file through a single handle: one open per call
/// covers length, seek and read. Never writes, never locks the game out.
/// </summary>
public sealed class LocalLogReader : ILogReader
{
    private readonly LocalLogReaderOptions _options;

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
            byte[] buffer;
            long start;

            try
            {
                var length = stream.Length;
                if (length == 0)
                {
                    return LogReadResult.Empty();
                }

                start = Math.Max(0, length - _options.MaxBytes);
                var take = (int)Math.Min(length - start, _options.MaxBytes);
                buffer = new byte[take];

                stream.Seek(start, SeekOrigin.Begin);
                var read = 0;
                while (read < take)
                {
                    var chunk = await stream.ReadAsync(buffer.AsMemory(read, take - read), cancellationToken);
                    if (chunk == 0)
                    {
                        break;
                    }

                    read += chunk;
                }

                if (read < take)
                {
                    Array.Resize(ref buffer, read);
                }
            }
            catch (OperationCanceledException)
            {
                return LogReadResult.Cancelled();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return LogReadResult.Failed(exception.Message);
            }

            var (text, _) = Decode(buffer);
            var truncated = start > 0;

            if (start > 0)
            {
                // The window may begin mid-line; drop the fragment so parsing
                // never invents an entry from a partial prefix.
                var newline = text.IndexOf('\n');
                text = newline >= 0 ? text[(newline + 1)..] : string.Empty;
            }

            var entries = LogParsers.For(source.Kind).Parse(text, headTruncated: start > 0);

            if (entries.Count > _options.MaxEntries)
            {
                entries = entries.Skip(entries.Count - _options.MaxEntries).ToList();
                truncated = true;
            }

            var capped = new List<LogEntry>(entries.Count);
            var cappedAny = false;
            foreach (var entry in entries)
            {
                var cappedEntry = Cap(entry);
                if (!ReferenceEquals(cappedEntry, entry))
                {
                    cappedAny = true;
                }

                capped.Add(cappedEntry);
            }

            return LogReadResult.Ok(capped, truncated || cappedAny);
        }
    }

    private LogEntry Cap(LogEntry entry)
    {
        if (entry.Message.Length <= _options.MaxEntryChars && entry.Raw.Length <= _options.MaxEntryChars)
        {
            return entry;
        }

        return entry with
        {
            Message = Cut(entry.Message),
            Raw = Cut(entry.Raw)
        };
    }

    private string Cut(string text) =>
        text.Length <= _options.MaxEntryChars ? text : text[.._options.MaxEntryChars];

    private static (string Text, bool HadBom) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(false, false).GetString(bytes, 3, bytes.Length - 3), true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), true);
        }

        return (new UTF8Encoding(false, false).GetString(bytes), false);
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
