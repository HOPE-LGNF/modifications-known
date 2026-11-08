using System.Net;
using System.Text.RegularExpressions;

namespace ModificationsKnown.Core.Nexus;

public enum NexusModSort
{
    Endorsements,
    Downloads,
    New,
    Updated
}

public sealed record NexusSearchQuery(
    string? Text = null,
    int Page = 1,
    int PageSize = 24,
    NexusModSort Sort = NexusModSort.Endorsements,
    bool IncludeAdult = false)
{
    public const int MaxPageSize = 100;

    public NexusSearchQuery Normalize() => this with
    {
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize),
        Text = string.IsNullOrWhiteSpace(Text) ? null : Text.Trim()
    };
}

public sealed record NexusModSummary(
    string Id,
    string GameScopedId,
    string Name,
    string? Summary,
    string? Version,
    string? Author,
    string? Category,
    string? ThumbnailUrl,
    long Endorsements,
    long Downloads,
    long FileSizeKilobytes,
    DateTimeOffset? UpdatedAt,
    bool Adult,
    bool SupportsVortex)
{
    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? "版本未知" : $"v{Version}";

    public string DownloadsLabel => FormatCount(Downloads);

    public string EndorsementsLabel => FormatCount(Endorsements);

    public string FileSizeLabel => FileSizeKilobytes <= 0
        ? "大小未知"
        : FileSizeKilobytes >= 1024
            ? $"{FileSizeKilobytes / 1024.0:F1} MB"
            : $"{FileSizeKilobytes} KB";

    public string UpdatedLabel => UpdatedAt is null
        ? "更新时间未知"
        : $"更新于 {UpdatedAt.Value.LocalDateTime:yyyy-MM-dd}";

    private static string FormatCount(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000.0:F1}M",
        >= 1_000 => $"{value / 1_000.0:F1}K",
        _ => value.ToString()
    };
}

public sealed record NexusModDetails(
    NexusModSummary Summary,
    string? Description,
    IReadOnlyList<string> GalleryUrls,
    bool AdultContent)
{
    public string DescriptionText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Description))
                return "这个模组还没有填写描述。";

            var text = Regex.Replace(Description, @"\[url=[^\]]*\]", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\[/?[a-zA-Z*][^\]]*\]", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", "", RegexOptions.IgnoreCase);
            text = WebUtility.HtmlDecode(text);
            return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        }
    }
}

public sealed record NexusModFile(
    string Id,
    string Name,
    bool IsActive,
    DateTimeOffset? LastUploadedAt,
    int VersionsCount,
    bool AllowModManagerDownload = true,
    bool PrimaryModManagerDownload = false)
{
    public string UploadedLabel => LastUploadedAt is null
        ? "上传时间未知"
        : LastUploadedAt.Value.LocalDateTime.ToString("yyyy-MM-dd");

    public bool IsManualOnly => !AllowModManagerDownload;

    public string ManagerDownloadLabel => AllowModManagerDownload
        ? PrimaryModManagerDownload ? "管理器默认" : "支持管理器"
        : "仅手动下载";
}

public sealed record NexusFileVersion(
    string Id,
    string GameScopedId,
    string Name,
    string Version,
    string Category,
    DateTimeOffset? UploadedAt,
    bool IsPrimary)
{
    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? "版本未知" : $"v{Version}";

    public string CategoryLabel => Category switch
    {
        "main" => "主文件",
        "update" => "更新",
        "optional" => "可选",
        "old_version" => "旧版本",
        "miscellaneous" => "杂项",
        "archived" => "归档",
        "removed" => "已移除",
        _ => Category
    };
}

public sealed record NexusDownloadLink(string Name, string Url);

public sealed record NexusAccountStatus(
    string? Name,
    long? UserId,
    bool IsPremium,
    bool IsSupporter,
    long? DailyRemaining,
    long? HourlyRemaining)
{
    public string TierLabel => IsPremium ? "Premium" : "免费账号";

    public string QuotaLabel
    {
        get
        {
            if (DailyRemaining is null && HourlyRemaining is null) return "配额未知";
            return $"剩余：{HourlyRemaining?.ToString() ?? "?"}/小时，{DailyRemaining?.ToString() ?? "?"}/天";
        }
    }
}

public sealed record NexusSearchResult(
    IReadOnlyList<NexusModSummary> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}
