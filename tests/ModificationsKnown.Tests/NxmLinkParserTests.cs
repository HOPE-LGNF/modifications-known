using ModificationsKnown.Core.Nexus;

namespace ModificationsKnown.Tests;

public sealed class NxmLinkParserTests
{
    [Fact]
    public void ParsesValidModFileLinkWithKey()
    {
        var raw = "nxm://scavprototype/mods/67/files/123456?key=abc%2Bdef%2Fghi&expires=1893456000&user_id=42";

        var ok = NxmLinkParser.TryParse(raw, out var link, out var error);

        Assert.True(ok, error);
        Assert.NotNull(link);
        Assert.Equal("scavprototype", link!.GameDomain);
        Assert.Equal(67, link.ModId);
        Assert.Equal(123456, link.FileId);
        Assert.Equal("abc+def/ghi", link.Key);
        Assert.Equal(1893456000, link.Expires);
        Assert.True(link.HasKey);
    }

    [Fact]
    public void AcceptsPremiumLinkWithoutKey()
    {
        var raw = "nxm://scavprototype/mods/67/files/123456";

        Assert.True(NxmLinkParser.TryParse(raw, out var link, out var error), error);
        Assert.NotNull(link);
        Assert.Null(link!.Key);
        Assert.False(link.HasKey);
    }

    [Theory]
    [InlineData("https://example.com/mods/1/files/2")]
    [InlineData("nxm://scavprototype/mods/1")]
    [InlineData("nxm://scavprototype/mods/0/files/2")]
    [InlineData("nxm://scavprototype/mods/1/files/-2")]
    [InlineData("nxm://scavprototype/collections/foo/revisions/1")]
    public void RejectsUnsupportedLinks(string raw)
    {
        Assert.False(NxmLinkParser.TryParse(raw, out _, out _));
    }

    [Theory]
    [InlineData("nxm://scavprototype/mods/1/files/2?key=abc")]
    [InlineData("nxm://scavprototype/mods/1/files/2?expires=1893456000")]
    [InlineData("nxm://scavprototype/mods/1/files/2?key=abc&expires=soon")]
    public void RejectsIncompleteOrInvalidCredentials(string raw)
    {
        Assert.False(NxmLinkParser.TryParse(raw, out _, out _));
    }
}
