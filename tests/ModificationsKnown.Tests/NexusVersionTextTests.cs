using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Nexus;

namespace ModificationsKnown.Tests;

public sealed class NexusVersionTextTests
{
    [Fact]
    public void ExtractsSemanticVersionFromVersionNameWhenApiVersionIsVPrefixOnly()
    {
        var version = new NexusFileVersion(
            "1", "2", "Warning Editor 1.8.3-v1", "v1", "Main", null, true);
        var file = new NexusModFile("f", "Warning Editor 1.8.3-v1", true, null, 1);

        Assert.Equal("1.8.3-v1", NexusVersionText.Resolve(version, file));
        Assert.False(VersionText.IsNewer("1.8.1-v2", "1.8.3-v1"));
    }
}
