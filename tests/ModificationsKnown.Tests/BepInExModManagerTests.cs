using ModificationsKnown.Core.Games;
using ModificationsKnown.Infrastructure.BepInEx;

namespace ModificationsKnown.Tests;

public sealed class BepInExModManagerTests
{
    private static GameInstallation CreateInstallation(string gameDirectory) => new(
        KnownGameProfiles.Full,
        gameDirectory,
        null,
        HasBepInEx: true,
        HasPluginsDirectory: true);

    [Fact]
    public async Task ScanGroupsAllDllsInFolderAsOnePackage()
    {
        using var temp = new TempDirectory();
        var pack = temp.CreateDirectory("BepInEx", "plugins", "TestPack");
        temp.CreateFile("BepInEx/plugins/TestPack/Main.dll", "not a real assembly");
        temp.CreateFile("BepInEx/plugins/TestPack/Dependency.dll", "not a real assembly");

        var manager = new BepInExModManager();
        var mods = await manager.ScanAsync(CreateInstallation(temp.Path));

        var mod = Assert.Single(mods);
        Assert.Equal("TestPack", mod.PackageId);
        Assert.Equal(2, mod.PluginCount);
        Assert.True(mod.CanDelete);
    }

    [Fact]
    public async Task ToggleMovesAllDllsTogether()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("BepInEx", "plugins", "TestPack");
        var first = temp.CreateFile("BepInEx/plugins/TestPack/Main.dll");
        var second = temp.CreateFile("BepInEx/plugins/TestPack/Dependency.dll");

        var manager = new BepInExModManager();
        var mod = Assert.Single(await manager.ScanAsync(CreateInstallation(temp.Path)));

        var disabled = await manager.SetEnabledAsync(mod, false);
        Assert.True(disabled.Success);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(first + ".disabled"));
        Assert.True(File.Exists(second + ".disabled"));

        var rescanned = Assert.Single(await manager.ScanAsync(CreateInstallation(temp.Path)));
        Assert.False(rescanned.IsEnabled);

        var enabled = await manager.SetEnabledAsync(rescanned, true);
        Assert.True(enabled.Success);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task DeleteRemovesPackageButNotLooseRootDll()
    {
        using var temp = new TempDirectory();
        var pack = temp.CreateDirectory("BepInEx", "plugins", "TestPack");
        temp.CreateFile("BepInEx/plugins/TestPack/Main.dll");
        var loose = temp.CreateFile("BepInEx/plugins/Loose.dll");

        var manager = new BepInExModManager();
        var mods = await manager.ScanAsync(CreateInstallation(temp.Path));
        var package = Assert.Single(mods, mod => mod.PackageId == "TestPack");
        var looseMod = Assert.Single(mods, mod => mod.PackageId == "Loose");

        Assert.False(looseMod.CanDelete);
        Assert.False((await manager.DeleteAsync(looseMod)).Success);

        Assert.True((await manager.DeleteAsync(package)).Success);
        Assert.False(Directory.Exists(pack));
        Assert.True(File.Exists(loose));
    }
}
