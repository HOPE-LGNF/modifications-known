using ModificationsKnown.Core.Games;
using ModificationsKnown.Infrastructure.BepInEx;

namespace ModificationsKnown.Tests;

public sealed class LooseDllAdoptionTests
{
    private static GameInstallation Game(string root) =>
        new(KnownGameProfiles.Full, root, null, HasBepInEx: true, HasPluginsDirectory: true);

    [Fact]
    public async Task AdoptsOneLooseDllAndDoesNotMergeOtherLooseDlls()
    {
        using var temp = new TempDirectory();
        var plugins = temp.CreateDirectory("BepInEx", "plugins");
        File.WriteAllText(Path.Combine(plugins, "Alpha.dll"), "alpha");
        File.WriteAllText(Path.Combine(plugins, "Alpha.pdb"), "pdb");
        File.WriteAllText(Path.Combine(plugins, "Beta.dll"), "beta");

        var manager = new BepInExModManager();
        var alpha = Assert.Single(
            await manager.ScanAsync(Game(temp.Path)),
            mod => string.Equals(mod.Plugins.FirstOrDefault()?.FileName, "Alpha.dll", StringComparison.OrdinalIgnoreCase));
        Assert.True(alpha.CanAdopt);

        var result = await manager.AdoptAsync(Game(temp.Path), alpha);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(plugins, "Alpha", "Alpha.dll")));
        Assert.True(File.Exists(Path.Combine(plugins, "Alpha", "Alpha.pdb")));
        Assert.True(File.Exists(Path.Combine(plugins, "Beta.dll")));
        Assert.False(File.Exists(Path.Combine(plugins, "Alpha.dll")));
    }

    [Fact]
    public async Task AdoptsDirectoryContainingOnlyDisabledDll()
    {
        using var temp = new TempDirectory();
        var package = temp.CreateDirectory("BepInEx", "plugins", "DisabledMod");
        File.WriteAllText(Path.Combine(package, "DisabledMod.dll.disabled"), "disabled");

        var manager = new BepInExModManager();
        var mod = Assert.Single(await manager.ScanAsync(Game(temp.Path)), candidate => candidate.PackageId == "DisabledMod");
        Assert.True(mod.CanAdopt);

        var result = await manager.AdoptAsync(Game(temp.Path), mod);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(package, ".modifications-known", "manifest.json")));
    }

    [Fact]
    public async Task AdoptsDisabledLooseDllAndKeepsItDisabled()
    {
        using var temp = new TempDirectory();
        var plugins = temp.CreateDirectory("BepInEx", "plugins");
        File.WriteAllText(Path.Combine(plugins, "Loose.dll.disabled"), "disabled");

        var manager = new BepInExModManager();
        var mod = Assert.Single(await manager.ScanAsync(Game(temp.Path)));
        Assert.True(mod.CanAdopt);

        var result = await manager.AdoptAsync(Game(temp.Path), mod);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(plugins, "Loose", "Loose.dll.disabled")));
        Assert.False(File.Exists(Path.Combine(plugins, "Loose.dll.disabled")));
    }

}
