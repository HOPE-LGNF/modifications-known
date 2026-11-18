namespace ModificationsKnown.Core.Saves;

/// <summary>
/// Sends a directory to the Windows recycle bin. Implementations must never
/// fall back to a permanent delete: a false result means the directory and the
/// index entry must stay exactly as they were.
/// </summary>
public interface IRecycleBin
{
    bool TrySendDirectory(string path, out string error);
}
