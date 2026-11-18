namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// Thrown when a stream produced more bytes than the caller allowed. It derives
/// from <see cref="IOException"/> so callers still see a stream failure, while
/// the save reader can tell "over the limit" apart from "not a valid gzip/JSON
/// payload" without matching localized message text.
/// </summary>
internal sealed class StreamLimitExceededException : IOException
{
    public StreamLimitExceededException(string message)
        : base(message)
    {
    }
}
