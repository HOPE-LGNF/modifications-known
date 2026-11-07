using ModificationsKnown.Core.Common;

namespace ModificationsKnown.Core.Network;

public interface IFileDownloader
{
    Task<OperationResult> DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
