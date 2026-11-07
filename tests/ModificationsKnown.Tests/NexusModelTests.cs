using ModificationsKnown.Core.Nexus;

namespace ModificationsKnown.Tests;

public sealed class NexusModelTests
{
    [Fact]
    public void SummaryFormatsCountsAndSize()
    {
        var summary = new NexusModSummary(
            "1",
            "2",
            "A Mod",
            "Summary",
            "1.2.3",
            "Author",
            "Gameplay",
            null,
            1200,
            1500000,
            2048,
            DateTimeOffset.UtcNow,
            false,
            true);

        Assert.Equal("v1.2.3", summary.VersionLabel);
        Assert.Equal("1.5M", summary.DownloadsLabel);
        Assert.Equal("1.2K", summary.EndorsementsLabel);
        Assert.Equal("2.0 MB", summary.FileSizeLabel);
    }

    [Fact]
    public void SearchResultComputesPages()
    {
        var result = new NexusSearchResult([], 1, 24, 50);
        Assert.Equal(3, result.TotalPages);
    }
}
