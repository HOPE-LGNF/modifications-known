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
    public void CreateJunction_TreatsShellMetacharactersAsLiteralPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        // These names used to be interpolated into "cmd.exe /c mklink /J \"...\"".
        var target = temp.CreateDirectory("target %PATH% & (x) 中文 'q'");
        File.WriteAllText(Path.Combine(target, "save.sv"), "payload");
        var link = Path.Combine(temp.Path, "live %OS% & echo injected (1) 中文");
        var marker = Path.Combine(temp.Path, "injected");

        var linker = new WindowsJunctionLinker();
        Assert.True(linker.CreateJunction(link, target, out var createError), createError);

        Assert.True(Directory.Exists(link));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(link, "save.sv")));
        Assert.True(linker.TryGetTarget(link, out var readTarget));
        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(readTarget));

        // No command interpreter ran: the "echo injected" fragment created nothing.
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(marker));

        Assert.True(linker.RemoveJunction(link, out var removeError), removeError);
        Assert.True(Directory.Exists(target));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(target, "save.sv")));
    }

    [Fact]
    public void CreateJunction_MissingTarget_FailsWithoutLeavingALink()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var link = Path.Combine(temp.Path, "live");
        var linker = new WindowsJunctionLinker();

        Assert.False(linker.CreateJunction(link, Path.Combine(temp.Path, "missing"), out var error));
        Assert.Contains("目标目录不存在", error);
        Assert.False(Directory.Exists(link));
    }

    [Fact]
    public void CreateJunction_ExistingDirectory_IsNotOverwritten()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var target = temp.CreateDirectory("target");
        var link = temp.CreateDirectory("live");
        File.WriteAllText(Path.Combine(link, "keep.txt"), "keep");

        var linker = new WindowsJunctionLinker();
        Assert.False(linker.CreateJunction(link, target, out var error));
        Assert.Contains("已存在", error);
        Assert.False(linker.TryGetTarget(link, out _));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(link, "keep.txt")));
    }

    [Fact]
    public void Exists_ReturnsFalseForMissingPath()
    {
        using var temp = new TempDirectory();
        var linker = new WindowsJunctionLinker();
        Assert.False(linker.Exists(Path.Combine(temp.Path, "missing")));
    }
}
