using ModificationsKnown.Core.Updates;

namespace ModificationsKnown.Tests;

public sealed class ReleaseAssetPolicyTests
{
    private const string GoodDigest = "sha256:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string GoodUrl = "https://github.com/HOPE-LGNF/modifications-known/releases/download/v0.7.0/ModificationsKnown-v0.7.0-win-x64.exe";

    private static ReleaseAsset Asset(string name = "ModificationsKnown-v0.7.0-win-x64.exe", string? digest = GoodDigest, string? url = GoodUrl) =>
        new(name, digest, url);

    [Fact]
    public void UniqueWinX64ExeIsInstallable()
    {
        Assert.True(ReleaseAssetPolicy.TrySelectInstallable([Asset()], out var selected, out var reason));
        Assert.NotNull(selected);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void OtherArchitecturesAreNotInstallable()
    {
        Assert.False(ReleaseAssetPolicy.TrySelectInstallable(
            [Asset(name: "ModificationsKnown-v0.7.0-win-arm64.exe")], out _, out var reason));
        Assert.Contains("win-x64", reason);
    }

    [Fact]
    public void MultipleWinX64ExesAreAmbiguous()
    {
        Assert.False(ReleaseAssetPolicy.TrySelectInstallable(
            [Asset(), Asset(name: "ModificationsKnown-v0.7.0-win-x64-extra.exe")], out _, out var reason));
        Assert.Contains("多个", reason);
    }

    [Fact]
    public void MissingOrInvalidDigestDisablesAutoInstall()
    {
        Assert.False(ReleaseAssetPolicy.TrySelectInstallable([Asset(digest: null)], out _, out _));
        Assert.False(ReleaseAssetPolicy.TrySelectInstallable([Asset(digest: "sha256:xyz")], out _, out _));
        Assert.False(ReleaseAssetPolicy.TrySelectInstallable([Asset(digest: "md5:" + new string('a', 64))], out _, out _));
        Assert.False(ReleaseAssetPolicy.TrySelectInstallable([Asset(digest: "sha256:" + new string('a', 63))], out _, out _));
    }

    [Theory]
    [InlineData("http://github.com/HOPE-LGNF/modifications-known/releases/download/v0.7.0/x.exe")]
    [InlineData("https://evil.example/HOPE-LGNF/modifications-known/releases/download/v0.7.0/x.exe")]
    [InlineData("https://github.com/other/repo/releases/download/v0.7.0/x.exe")]
    [InlineData("https://github.com/HOPE-LGNF/modifications-known/releases/download/")]
    public void UntrustedDownloadUrlsAreRejected(string url)
    {
        Assert.False(ReleaseAssetPolicy.IsTrustedDownloadUrl(url));
    }

    [Fact]
    public void TrustedReleasePageUrlsAreAccepted()
    {
        Assert.True(ReleaseAssetPolicy.IsTrustedReleaseUrl(
            "https://github.com/HOPE-LGNF/modifications-known/releases/tag/v0.7.0"));
        Assert.False(ReleaseAssetPolicy.IsTrustedReleaseUrl(
            "https://github.com/other/repo/releases/tag/v0.7.0"));
    }

    [Fact]
    public void DigestMustBeSixtyFourHexCharacters()
    {
        Assert.True(ReleaseAssetPolicy.TryGetSha256(GoodDigest, out var value));
        Assert.Equal(64, value.Length);
        Assert.False(ReleaseAssetPolicy.TryGetSha256("sha256:" + new string('z', 64), out _));
    }
}
