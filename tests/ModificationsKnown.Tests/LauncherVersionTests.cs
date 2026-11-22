using ModificationsKnown.Core.Updates;

namespace ModificationsKnown.Tests;

public sealed class LauncherVersionTests
{
    private static LauncherVersion Parse(string text)
    {
        Assert.True(LauncherVersion.TryParse(text, out var version), $"'{text}' should parse");
        return version;
    }

    [Theory]
    [InlineData("0.7.0")]
    [InlineData("v0.7.0")]
    [InlineData("V0.7.0")]
    public void AcceptsPlainVersionWithOptionalSinglePrefix(string text)
    {
        Assert.Equal("0.7.0", Parse(text).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("vv0.7.0")]
    [InlineData("v")] 
    [InlineData("0.7")]
    [InlineData("0.7.0.1")]
    [InlineData("0.7.x")]
    [InlineData("0.07.0")]
    [InlineData("0.7.0-")]
    [InlineData("0.7.0-beta..2")]
    [InlineData("0.7.0-beta_1")]
    [InlineData("0.7.0+bad..meta")]
    [InlineData("0.99999999999.0")]
    public void RejectsMalformedVersions(string text)
    {
        Assert.False(LauncherVersion.TryParse(text, out _), $"'{text}' should not parse");
    }

    [Fact]
    public void RejectsAbsurdlyLongInput()
    {
        var text = "1.2.3-" + new string('a', 300);
        Assert.False(LauncherVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("0.7.0-beta", "0.7.0")]
    [InlineData("0.7.0-alpha", "0.7.0-beta")]
    [InlineData("0.7.0-beta", "0.7.0-rc.1")]
    [InlineData("0.8.0-rc.1", "0.8.0")]
    [InlineData("0.8.0", "0.8.1-beta.1")]
    [InlineData("0.7.0-beta.2", "0.7.0-beta.10")]
    [InlineData("0.7.0-beta", "0.7.0-beta.2")]
    [InlineData("0.7.0-beta.2", "0.7.0-beta.2.1")]
    [InlineData("0.7.0-beta.2", "0.7.0-beta-1")]
    public void OrdersVersionsBySemverPrecedence(string lower, string higher)
    {
        Assert.True(Parse(lower).CompareTo(Parse(higher)) < 0, $"{lower} < {higher}");
        Assert.True(Parse(higher).CompareTo(Parse(lower)) > 0, $"{higher} > {lower}");
    }

    [Fact]
    public void NumericIdentifiersCompareNumericallyNotLexically()
    {
        Assert.True(Parse("0.7.0-beta.9").CompareTo(Parse("0.7.0-beta.10")) < 0);
    }

    [Fact]
    public void IgnoresBuildMetadataForOrdering()
    {
        Assert.Equal(0, Parse("0.8.0-beta.2+abc").CompareTo(Parse("0.8.0-beta.2+def")));
    }

    [Fact]
    public void KeepsBuildMetadataForDisplay()
    {
        Assert.Equal("0.8.0-beta.2+abc", Parse("v0.8.0-beta.2+abc").ToString());
    }

    [Fact]
    public void SameVersionDifferentMetadataIsNotNewer()
    {
        Assert.Equal(0, Parse("0.7.0+1").CompareTo(Parse("0.7.0+2")));
    }

    [Fact]
    public void PrereleaseIsRecognised()
    {
        Assert.True(Parse("0.8.0-beta.2").HasPrerelease);
        Assert.False(Parse("0.8.0").HasPrerelease);
    }
}
