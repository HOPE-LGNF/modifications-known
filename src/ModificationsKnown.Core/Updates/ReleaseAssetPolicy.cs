namespace ModificationsKnown.Core.Updates;

/// <summary>
/// Auto-install eligibility. A release may offer a manual download link and
/// still fail these checks; that only removes the one-click install, never the
/// "open the release page" path.
/// </summary>
public static class ReleaseAssetPolicy
{
    public const string RepositoryOwner = "HOPE-LGNF";
    public const string RepositoryName = "modifications-known";
    public const string RepositorySlug = RepositoryOwner + "/" + RepositoryName;

    /// <summary>Exactly one exe, named for win-x64, with a sha256 digest and a
    /// GitHub download URL for the fixed repository.</summary>
    public static bool TrySelectInstallable(
        IReadOnlyList<ReleaseAsset>? assets,
        out ReleaseAsset? asset,
        out string reason)
    {
        asset = null;

        if (assets is null || assets.Count == 0)
        {
            reason = "这个 Release 没有可下载资产。";
            return false;
        }

        var candidates = assets
            .Where(candidate => IsWinX64Exe(candidate.Name))
            .ToList();

        if (candidates.Count == 0)
        {
            reason = "这个 Release 没有 win-x64 的可执行文件。";
            return false;
        }

        if (candidates.Count > 1)
        {
            reason = "这个 Release 有多个 win-x64 可执行文件，无法确定要安装哪一个。";
            return false;
        }

        var candidate = candidates[0];

        if (!TryGetSha256(candidate.Digest, out _))
        {
            reason = "Release 未提供合法的 SHA-256，自动安装已禁用，请手动下载。";
            return false;
        }

        if (!IsTrustedDownloadUrl(candidate.DownloadUrl))
        {
            reason = "下载地址不是本仓库的 GitHub Release 链接，自动安装已禁用。";
            return false;
        }

        asset = candidate;
        reason = string.Empty;
        return true;
    }

    public static bool IsWinX64Exe(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && name.Contains("win-x64", StringComparison.OrdinalIgnoreCase);

    /// <summary>Accept only "sha256:" followed by 64 hex characters.</summary>
    public static bool TryGetSha256(string? digest, out string sha256)
    {
        sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(digest)) return false;
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return false;

        var value = digest["sha256:".Length..].Trim();
        if (value.Length != 64) return false;

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character)) return false;
        }

        sha256 = value.ToLowerInvariant();
        return true;
    }

    /// <summary>The release page link shown to users must be HTTPS on GitHub
    /// and belong to the fixed repository.</summary>
    public static bool IsTrustedReleaseUrl(string? url) =>
        MatchesRepositoryPath(url, $"/{RepositorySlug}/releases");

    public static bool IsTrustedDownloadUrl(string? url) =>
        MatchesRepositoryPath(url, $"/{RepositorySlug}/releases/download/");

    private static bool MatchesRepositoryPath(string? url, string requiredPrefix)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.AbsolutePath.Length > requiredPrefix.Length
               && uri.AbsolutePath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
