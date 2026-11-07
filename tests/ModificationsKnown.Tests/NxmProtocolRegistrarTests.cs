using ModificationsKnown.Infrastructure.Windows;

namespace ModificationsKnown.Tests;

public sealed class NxmProtocolRegistrarTests
{
    [Fact]
    public void ReportsUnsupportedOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
            return; // Registry behavior is verified on the Windows runner.

        var registrar = new NxmProtocolRegistrar();
        var status = registrar.GetStatus();

        Assert.False(status.Supported);
        Assert.False(status.RegisteredForCurrentExecutable);
        Assert.False(registrar.Register().Success);
    }
}
