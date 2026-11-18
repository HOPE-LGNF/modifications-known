using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

public sealed class WindowsJunctionLinkerTests
{
    [Fact]
    public void Create_Read_Remove_DoesNotTouchTarget()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var target = temp.CreateDirectory("target");
        var link = Path.Combine(temp.Path, "live");
        File.WriteAllText(Path.Combine(target, "save.sv"), "payload");

        var linker = new WindowsJunctionLinker();
        Assert.True(linker.CreateJunction(link, target, out var createError), createError);
        Assert.True(linker.TryGetTarget(link, out var readTarget));
        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(readTarget));

        Assert.True(linker.RemoveJunction(link, out var removeError), removeError);
        Assert.False(Directory.Exists(link));
        Assert.True(Directory.Exists(target));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(target, "save.sv")));
    }

    [Fact]
    public void Remove_RefusesRealDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var directory = temp.CreateDirectory("real");
        var linker = new WindowsJunctionLinker();

        Assert.False(linker.RemoveJunction(directory, out var error));
        Assert.Contains("不是链接", error);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void Exists_ReturnsFalseForMissingPath()
    {
        using var temp = new TempDirectory();
        var linker = new WindowsJunctionLinker();
        Assert.False(linker.Exists(Path.Combine(temp.Path, "missing")));
    }
}
