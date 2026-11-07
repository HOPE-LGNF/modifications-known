using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Infrastructure.BepInEx;

namespace ModificationsKnown.Tests;

public sealed class AdoptPackageTests
{
    private static GameInstallation CreateInstallation(string gameDirectory) => new(
        KnownGameProfiles.Full,
        gameDirectory,
        null,
        HasBepInEx: true,
        HasPluginsDirectory: true);

    [Fact]
    public async Task AdoptsUnmanagedPackageAndBecomesManaged()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("BepInEx", "plugins", "ManualMod");
        temp.CreateFile("BepInEx/plugins/ManualMod/ManualMod.dll", "fake dll");
        temp.CreateFile("BepInEx/plugins/ManualMod/readme.txt", "manual");
        var installation = CreateInstallation(temp.Path);
        var manager = new BepInExModManager();

        var before = Assert.Single(await manager.ScanAsync(installation));
        Assert.False(before.IsManaged);
        Assert.True(before.CanAdopt);
        Assert.False(before.CanDelete);

        var result = await manager.AdoptAsync(installation, before);

        Assert.True(result.Success, result.Message);
        var after = Assert.Single(await manager.ScanAsync(installation));
        Assert.True(after.IsManaged);
        Assert.Equal("已托管", after.ManagementLabel);
        Assert.True(after.CanDelete);
        Assert.False(after.CanAdopt);
        Assert.True(File.Exists(Path.Combine(
            installation.PluginsDirectory,
            "ManualMod",
            PackageManifest.DirectoryName,
            PackageManifest.FileName)));
    }

    [Fact]
    public async Task AdoptedPackageCanBeUninstalledByManifest()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("BepInEx", "plugins", "ManualMod");
        temp.CreateFile("BepInEx/plugins/ManualMod/ManualMod.dll", "fake dll");
        var installation = CreateInstallation(temp.Path);
        var manager = new BepInExModManager();
        var before = Assert.Single(await manager.ScanAsync(installation));
        await manager.AdoptAsync(installation, before);

        var managed = Assert.Single(await manager.ScanAsync(installation));
        var result = await manager.UninstallManagedAsync(installation, managed);

        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(Path.Combine(installation.PluginsDirectory, "ManualMod")));
    }
}
