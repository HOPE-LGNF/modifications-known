using Microsoft.VisualBasic.FileIO;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// Windows Shell recycle-bin integration. DeleteDirectory with
/// RecycleOption.SendToRecycleBin either succeeds or throws; this class never
/// retries with a permanent Directory.Delete.
/// </summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    public bool TrySendDirectory(string path, out string error)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                error = "目录不存在。";
                return false;
            }

            FileSystem.DeleteDirectory(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
