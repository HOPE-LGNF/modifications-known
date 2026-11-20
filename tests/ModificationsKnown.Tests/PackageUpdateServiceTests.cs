using System.IO.Compression;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Infrastructure.BepInEx;
using ModificationsKnown.Infrastructure.Installation;

namespace ModificationsKnown.Tests;

public sealed class PackageUpdateServiceTests
{
    private static GameInstallation Installation(string root) =>
        new(KnownGameProfiles.Full, root, null, HasBepInEx: true, HasPluginsDirectory: true);

    private static string Zip(TempDirectory temp, string name, params (string Path, string Content)[] files)
    {
        var archivePath = Path.Combine(temp.Path, name);
        using var stream = File.Create(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (path, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open());
            writer.Write(content);
        }

        return archivePath;
    }

    private static async Task<(PackageUpdateService Service, InstalledMod Target, GameInstallation Game)> SetupAsync(
        TempDirectory temp)
    {
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "patchers"));
        var old = Zip(temp, "old.zip", ("BepInEx/plugins/Together/Together.dll", "old"));
        var installer = new ArchiveModInstaller();
        var game = Installation(temp.Path);
        var oldResult = await installer.InstallArchiveAsync(
            game, old, source: PackageSource.Nexus("Together", "scavprototype", "67", "uid", "f1", "1.0"));
        Assert.True(oldResult.AllSucceeded, oldResult.Message);

        var manager = new BepInExModManager();
        var operations = new ModOperationService(manager, installer);
        var target = Assert.Single(await manager.ScanAsync(game), mod => mod.PackageId == "Together");
        return (new PackageUpdateService(operations), target, game);
    }

    [Fact]
    public async Task ReplacesPackageAndAuxiliaryFilesTogether()
    {
        using var temp = new TempDirectory();
        var (service, target, game) = await SetupAsync(temp);
        var archive = Zip(
            temp,
            "new.zip",
            ("BepInEx/plugins/Together/Together.dll", "new"),
            ("BepInEx/plugins/CUCoreLib.dll", "core"),
            ("BepInEx/patchers/CT_AutoUpdateApplier.dll", "patcher"));

        var result = await service.InstallReplacingAsync(
            game, target, archive,
            PackageSource.Nexus("Together", "scavprototype", "67", "uid", "f2", "1.1"),
            keepBackup: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal("new", File.ReadAllText(Path.Combine(temp.Path, "BepInEx", "plugins", "Together", "Together.dll")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "CUCoreLib.dll")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "BepInEx", "patchers", "CT_AutoUpdateApplier.dll")));
        Assert.False(Directory.Exists(target.DirectoryPath + ".mk-old-" + "*"));
    }

    [Fact]
    public async Task RestoresOldPackageWhenArchiveHasNoTargetPackage()
    {
        using var temp = new TempDirectory();
        var (service, target, game) = await SetupAsync(temp);
        var archive = Zip(temp, "wrong.zip", ("BepInEx/plugins/Other/Other.dll", "other"));

        var result = await service.InstallReplacingAsync(
            game, target, archive, PackageSource.Local, keepBackup: false);

        Assert.False(result.Success);
        Assert.Equal("old", File.ReadAllText(Path.Combine(temp.Path, "BepInEx", "plugins", "Together", "Together.dll")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "Together")));
    }

    [Fact]
    public async Task SameContentStillRecordsNewSourceVersion()
    {
        using var temp = new TempDirectory();
        var (service, target, game) = await SetupAsync(temp);
        var archive = Zip(temp, "same.zip", ("BepInEx/plugins/Together/Together.dll", "old"));

        var result = await service.InstallReplacingAsync(
            game,
            target,
            archive,
            PackageSource.Nexus("Together", "scavprototype", "67", "uid", "f3", "1.1"),
            keepBackup: false);

        Assert.True(result.Success, result.Message);
        Assert.False(result.Warning, result.Message);
        var refreshed = await new BepInExModManager().ScanAsync(game);
        var updated = Assert.Single(refreshed, mod => mod.PackageId == "Together");
        Assert.Equal("1.1", updated.UpdateVersion);
    }
}
