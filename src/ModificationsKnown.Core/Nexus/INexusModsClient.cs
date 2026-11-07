namespace ModificationsKnown.Core.Nexus;

public sealed class NexusApiException : Exception
{
    public NexusApiException(string message, int? statusCode = null, string? responseBody = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public int? StatusCode { get; }

    public string? ResponseBody { get; }

    public bool IsUnauthorized => StatusCode is 401 or 403;

    public bool IsNotFound => StatusCode == 404;
}

public interface INexusModsClient
{
    /// <summary>
    /// Catalog search. The implementation uses Nexus GraphQL for name search
    /// and falls back to the documented v3 search endpoint.
    /// </summary>
    Task<NexusSearchResult> SearchModsAsync(
        string gameDomain,
        NexusSearchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Details for a game-scoped mod id.</summary>
    Task<NexusModDetails> GetModAsync(
        string gameDomain,
        string gameScopedId,
        CancellationToken cancellationToken = default);

    /// <summary>Files belonging to a mod. The id is the composite mod UID.</summary>
    Task<IReadOnlyList<NexusModFile>> GetFilesAsync(
        string modId,
        CancellationToken cancellationToken = default);

    /// <summary>Versions belonging to a mod file id.</summary>
    Task<IReadOnlyList<NexusFileVersion>> GetFileVersionsAsync(
        string modFileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signed CDN links for a game-scoped file version id. Free accounts may get
    /// 403 unless the file has direct downloads enabled; callers should offer a
    /// browser fallback.
    /// </summary>
    Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(
        string gameDomain,
        string fileVersionGameScopedId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Legacy v1 download-link lookup used by NXM links. The NXM link supplies
    /// mod/file ids and, for free accounts, a short-lived key/expires pair.
    /// </summary>
    Task<IReadOnlyList<NexusDownloadLink>> GetNxmDownloadLinksAsync(
        string gameDomain,
        long modId,
        long fileId,
        string? key,
        long? expires,
        CancellationToken cancellationToken = default);
}
