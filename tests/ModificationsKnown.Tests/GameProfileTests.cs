using ModificationsKnown.Core.Games;

namespace ModificationsKnown.Tests;

public sealed class GameProfileTests
{
    [Fact]
    public void FullAndDemoUseRealDistinctSteamAppIds()
    {
        Assert.Equal("4576490", KnownGameProfiles.Full.SteamAppId);
        Assert.Equal("4576510", KnownGameProfiles.Demo.SteamAppId);
        Assert.NotEqual(KnownGameProfiles.Full.SteamAppId, KnownGameProfiles.Demo.SteamAppId);
    }

    [Fact]
    public void FindByAppIdDoesNotConfuseDemoAndFull()
    {
        Assert.Same(KnownGameProfiles.Full, KnownGameProfiles.FindByAppId("4576490"));
        Assert.Same(KnownGameProfiles.Demo, KnownGameProfiles.FindByAppId("4576510"));
    }
}
