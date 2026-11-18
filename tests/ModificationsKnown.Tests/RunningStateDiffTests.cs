using ModificationsKnown.Infrastructure.Diagnostics;

namespace ModificationsKnown.Tests;

public sealed class RunningStateDiffTests
{
    [Fact]
    public void HasChanged_BothEmpty_ReturnsFalse()
    {
        var active = new Dictionary<string, int>();
        var previous = new Dictionary<string, int>();

        Assert.False(RunningStateDiff.HasChanged(active, previous));
    }

    [Fact]
    public void HasChanged_InstallationsWithoutProcesses_ReturnsFalse()
    {
        // Two registered installations, none of them running: the scan sees
        // every installation but only running ones count as state.
        var active = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 0,
            [@"D:\Games\RimWorld-Steam"] = 0
        };
        var previous = new Dictionary<string, int>();

        Assert.False(RunningStateDiff.HasChanged(active, previous));
    }

    [Fact]
    public void HasChanged_InactiveInstallationsInPrevious_ReturnsFalse()
    {
        // Previous snapshots may still carry explicit zero entries; they must
        // not look like a removed running installation.
        var active = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 0
        };
        var previous = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 0,
            [@"D:\Games\RimWorld-Steam"] = 0
        };

        Assert.False(RunningStateDiff.HasChanged(active, previous));
    }

    [Fact]
    public void HasChanged_OneInstallationStarted_ReturnsTrue()
    {
        var active = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 1
        };
        var previous = new Dictionary<string, int>();

        Assert.True(RunningStateDiff.HasChanged(active, previous));
    }

    [Fact]
    public void HasChanged_SameRunningInstallation_ReturnsFalse()
    {
        var active = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 1
        };
        var previous = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 1
        };

        Assert.False(RunningStateDiff.HasChanged(active, previous));
    }

    [Fact]
    public void HasChanged_DifferentInstallationRunning_ReturnsTrue()
    {
        var active = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 1
        };
        var previous = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld-Steam"] = 1
        };

        Assert.True(RunningStateDiff.HasChanged(active, previous));
    }

    [Fact]
    public void HasChanged_ProcessCountIncreased_ReturnsTrue()
    {
        var active = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 2
        };
        var previous = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 1
        };

        Assert.True(RunningStateDiff.HasChanged(active, previous));
    }

    [Fact]
    public void HasChanged_AllProcessesExited_ReturnsTrue()
    {
        var active = new Dictionary<string, int>();
        var previous = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 1
        };

        Assert.True(RunningStateDiff.HasChanged(active, previous));
    }

    [Fact]
    public void HasChanged_KeysDifferingOnlyByCase_ReturnsFalse()
    {
        // The monitors store paths that can differ in casing between scans;
        // the comparison must be case-insensitive even for ordinal dictionaries.
        var active = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 1
        };
        var previous = new Dictionary<string, int>
        {
            [@"d:\games\rimworld"] = 1
        };

        Assert.False(RunningStateDiff.HasChanged(active, previous));
    }

    [Fact]
    public void HasChanged_NewRunningKeyDiffersOnlyByCase_ReturnsTrue()
    {
        var active = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = 2
        };
        var previous = new Dictionary<string, int>
        {
            [@"d:\games\rimworld"] = 1
        };

        Assert.True(RunningStateDiff.HasChanged(active, previous));
    }

    [Fact]
    public void HasChanged_NegativePreviousCount_IsTreatedAsAbsent()
    {
        var active = new Dictionary<string, int>();
        var previous = new Dictionary<string, int>
        {
            [@"D:\Games\RimWorld"] = -1
        };

        Assert.False(RunningStateDiff.HasChanged(active, previous));
    }
}
