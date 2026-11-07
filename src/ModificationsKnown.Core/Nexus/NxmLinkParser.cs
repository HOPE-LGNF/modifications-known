namespace ModificationsKnown.Core.Nexus;

/// <summary>
/// Parses nxm:// links without touching the network. This is deliberately
/// strict: only the mod/file shape is accepted, and IDs must be positive.
/// Collection links are not supported yet.
/// </summary>
public static class NxmLinkParser
{
    private const int MaximumLength = 4096;

    public static bool TryParse(string? raw, out NxmLink? link, out string error)
    {
        link = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "NXM 链接为空。";
            return false;
        }

        var value = raw.Trim().Trim('"');

        // Links copied from page HTML often contain &amp; instead of &. Treat
        // the copied attribute as a link, not as XML text.
        value = value.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);

        if (value.Length > MaximumLength)
        {
            error = "NXM 链接过长，已拒绝。";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "nxm", StringComparison.OrdinalIgnoreCase))
        {
            error = "不是有效的 nxm:// 链接。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host)
            || uri.Host.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-')))
        {
            error = "NXM 链接中的游戏域无效。";
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4
            || !string.Equals(segments[0], "mods", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], "files", StringComparison.OrdinalIgnoreCase))
        {
            error = "暂不支持这种 NXM 链接；目前只接受 mods/{id}/files/{id}。";
            return false;
        }

        if (!long.TryParse(segments[1], out var modId) || modId <= 0
            || !long.TryParse(segments[3], out var fileId) || fileId <= 0)
        {
            error = "NXM 链接中的 mod/file id 无效。";
            return false;
        }

        string? key = null;
        long? expires = null;
        long? userId = null;

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;

            var name = Uri.UnescapeDataString(part[..separator]);
            var queryValue = Uri.UnescapeDataString(part[(separator + 1)..]);

            if (string.Equals(name, "key", StringComparison.OrdinalIgnoreCase))
                key = queryValue;
            else if (string.Equals(name, "expires", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(queryValue, out var parsedExpiry) || parsedExpiry <= 0)
                {
                    error = "NXM 链接中的 expires 无效。";
                    return false;
                }

                expires = parsedExpiry;
            }
            else if (string.Equals(name, "user_id", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(queryValue, out var parsedUser) && parsedUser > 0)
                    userId = parsedUser;
            }
        }

        if (key is not null && expires is null)
        {
            error = "NXM 链接包含 key 但缺少 expires，已拒绝。";
            return false;
        }

        if (expires is not null && key is null)
        {
            error = "NXM 链接包含 expires 但缺少 key，已拒绝。";
            return false;
        }

        link = new NxmLink(uri.Host.ToLowerInvariant(), modId, fileId, key, expires, userId);
        return true;
    }
}
