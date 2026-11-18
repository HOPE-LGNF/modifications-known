namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// Read-only wrapper that fails as soon as more than <paramref name="limit"/>
/// bytes are produced by the inner stream. Used for ZIP entries, gzip payloads
/// and manifests so a declared (or lying) length can never turn into an
/// unbounded allocation. The stream is never seekable: both ZIP and GZip entry
/// streams are forward-only, and refusing to seek keeps callers honest.
/// </summary>
internal sealed class CappedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private readonly string _description;
    private long _total;

    public CappedReadStream(Stream inner, long limit, string description)
    {
        _inner = inner;
        _limit = limit;
        _description = description;
    }

    public long TotalRead => _total;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _total;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Track(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Track(read);
        return read;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ReadAsyncCore(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsyncCore(buffer, offset, count, cancellationToken);

    public override int ReadByte()
    {
        var value = _inner.ReadByte();
        if (value >= 0) Track(1);
        return value;
    }

    private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Track(read);
        return read;
    }

    private async Task<int> ReadAsyncCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        Track(read);
        return read;
    }

    private void Track(int read)
    {
        if (read <= 0) return;

        _total += read;
        if (_total > _limit)
        {
            throw new StreamLimitExceededException(
                $"{_description}超过允许的大小上限（{_limit} 字节），已停止读取。");
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
