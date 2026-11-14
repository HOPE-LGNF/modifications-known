using ModificationsKnown.Core.Common;

namespace ModificationsKnown.Tests;

public sealed class VersionTextTests
{
    [Theory]
    [InlineData("1.1.2", "v1.1.2")]
    [InlineData("v1.1.2", "v1.1.2")]
    [InlineData(" V2.0 ", "v2.0")]
    [InlineData("", "版本未知")]
    [InlineData(null, "版本未知")]
    public void LabelAddsSinglePrefix(string? version, string expected) =>
        Assert.Equal(expected, VersionText.Label(version));

    [Theory]
    [InlineData("1.1.2", "1.1.1", true)]
    [InlineData("v1.1.2", "1.1.2", false)]
    [InlineData("1.2", "1.10", false)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0", null, true)]
    public void IsNewerComparesNumericParts(string? candidate, string? current, bool expected) =>
        Assert.Equal(expected, VersionText.IsNewer(candidate, current));
}
