using System.IO.Compression;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Infrastructure.Installation;
using ModificationsKnown.Infrastructure.BepInEx;

namespace ModificationsKnown.Tests;

public sealed class ArchiveModInstallerTests
{
    private static GameInstallation CreateInstallation(string gameDirectory) => new(
        KnownGameProfiles.Full,
        gameDirectory,
        null,
        HasBepInEx: true,
        HasPluginsDirectory: true);

    private static string CreateArchive(TempDirectory temp, string name, params (string Path, string Content)[] files)
    {
        var archivePath = Path.Combine(temp.Path, name);
        using var stream = File.Create(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(file.Path);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(file.Content);
        }

        return archivePath;
    }

    [Fact]
    public async Task InstallsBepInExPluginLayout()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins"));
        var archive = CreateArchive(
            temp,
            "cool.zip",
            ("BepInEx/plugins/CoolMod/CoolMod.dll", "fake dll"),
            ("BepInEx/plugins/CoolMod/data.txt", "asset"));

        var installer = new ArchiveModInstaller();
        var result = await installer.InstallArchiveAsync(CreateInstallation(temp.Path), archive);

        Assert.True(result.Success, result.Message);
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "CoolMod")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "CoolMod", "data.txt")));
    }

    [Fact]
    public async Task RejectsPathTraversalEntry()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins"));
        var archive = CreateArchive(
            temp,
            "evil.zip",
            ("../escape.txt", "escape"),
            ("BepInEx/plugins/Evil/Evil.dll", "fake dll"));

        var installer = new ArchiveModInstaller();
        var result = await installer.InstallArchiveAsync(CreateInstallation(temp.Path), archive);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(temp.Path, "escape.txt")));
    }

    [Fact]
    public async Task DoesNotOverwriteExistingPackage()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins"));
        var existing = Path.Combine(temp.Path, "BepInEx", "plugins", "CoolMod");
        Directory.CreateDirectory(existing);
        var archive = CreateArchive(
            temp,
            "cool.zip",
            ("BepInEx/plugins/CoolMod/CoolMod.dll", "fake dll"));

        var installer = new ArchiveModInstaller();
        var result = await installer.InstallArchiveAsync(CreateInstallation(temp.Path), archive);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(existing));
    }

    [Fact]
    public async Task InstallsTargetPackageWithAuxiliaryFilesWhenAllowed()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "patchers"));
        var archive = CreateArchive(
            temp,
            "together.zip",
            ("BepInEx/plugins/Together/Together.dll", "fake dll"),
            ("BepInEx/plugins/CUCoreLib.dll", "fake core"),
            ("BepInEx/patchers/CT_AutoUpdateApplier.dll", "fake patcher"));

        var installer = new ArchiveModInstaller();
        var result = await installer.InstallArchiveAsync(
            CreateInstallation(temp.Path),
            archive,
            options: new ArchiveInstallOptions("Together", AllowAuxiliaryFiles: true));

        Assert.True(result.AllSucceeded, result.Message);
        Assert.True(File.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "Together", "Together.dll")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "CUCoreLib.dll")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "BepInEx", "patchers", "CT_AutoUpdateApplier.dll")));
        Assert.Contains("BepInEx/plugins/CUCoreLib.dll", result.AuxiliaryFiles!);
    }

    [Fact]
    public async Task RejectsLooseRootDllWithoutAuxiliaryOption()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins"));
        var archive = CreateArchive(
            temp,
            "together.zip",
            ("BepInEx/plugins/Together/Together.dll", "fake dll"),
            ("BepInEx/plugins/CUCoreLib.dll", "fake core"));

        var installer = new ArchiveModInstaller();
        var result = await installer.InstallArchiveAsync(CreateInstallation(temp.Path), archive);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "Together")));
    }

    [Fact]
    public async Task ReportsPartialArchiveAsNotAllSucceeded()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins", "Existing"));
        File.WriteAllText(Path.Combine(temp.Path, "BepInEx", "plugins", "Existing", "Existing.dll"), "old");
        var archive = CreateArchive(
            temp,
            "multi.zip",
            ("BepInEx/plugins/A/A.dll", "a"),
            ("BepInEx/plugins/Existing/Existing.dll", "new"));

        var installer = new ArchiveModInstaller();
        var result = await installer.InstallArchiveAsync(CreateInstallation(temp.Path), archive);

        Assert.True(result.Success);
        Assert.False(result.AllSucceeded);
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "A")));
    }


    [Fact]
    public async Task UninstallRemovesOwnedAuxiliaryFiles()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "patchers"));
        var archive = CreateArchive(
            temp,
            "together.zip",
            ("BepInEx/plugins/Together/Together.dll", "fake dll"),
            ("BepInEx/plugins/CUCoreLib.dll", "fake core"),
            ("BepInEx/patchers/CT_AutoUpdateApplier.dll", "fake patcher"));

        var installer = new ArchiveModInstaller();
        var installed = await installer.InstallArchiveAsync(
            CreateInstallation(temp.Path),
            archive,
            options: new ArchiveInstallOptions("Together", AllowAuxiliaryFiles: true));
        Assert.True(installed.AllSucceeded, installed.Message);

        var manager = new BepInExModManager();
        var scanned = await manager.ScanAsync(CreateInstallation(temp.Path));
        var together = Assert.Single(scanned, mod => mod.PackageId == "Together");
        Assert.Equal(2, together.AuxiliaryFiles.Count);

        var result = await manager.UninstallManagedAsync(CreateInstallation(temp.Path), together);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "CUCoreLib.dll")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "BepInEx", "patchers", "CT_AutoUpdateApplier.dll")));
    }


    [Fact]
    public async Task PreservesDisabledAuxiliaryFileAndHidesItFromLooseDlls()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "patchers"));
        File.WriteAllText(Path.Combine(temp.Path, "BepInEx", "plugins", "CUCoreLib.dll.disabled"), "old core");
        var archive = CreateArchive(
            temp,
            "together.zip",
            ("BepInEx/plugins/Together/Together.dll", "fake dll"),
            ("BepInEx/plugins/CUCoreLib.dll", "new core"));

        var installer = new ArchiveModInstaller();
        var installed = await installer.InstallArchiveAsync(
            CreateInstallation(temp.Path),
            archive,
            options: new ArchiveInstallOptions("Together", AllowAuxiliaryFiles: true));
        Assert.True(installed.AllSucceeded, installed.Message);
        Assert.True(File.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "CUCoreLib.dll.disabled")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "CUCoreLib.dll")));

        var manager = new BepInExModManager();
        var scanned = await manager.ScanAsync(CreateInstallation(temp.Path));
        Assert.DoesNotContain(scanned, mod =>
            string.Equals(mod.DirectoryPath, Path.Combine(temp.Path, "BepInEx", "plugins"), StringComparison.OrdinalIgnoreCase));
    }


    [Fact]
    public async Task InstallsPackageContainingOnlyDisabledDll()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins"));
        var archive = CreateArchive(
            temp,
            "disabled.zip",
            ("BepInEx/plugins/DisabledMod/DisabledMod.dll.disabled", "disabled"));

        var installer = new ArchiveModInstaller();
        var result = await installer.InstallArchiveAsync(CreateInstallation(temp.Path), archive);

        Assert.True(result.AllSucceeded, result.Message);
        Assert.True(File.Exists(Path.Combine(temp.Path, "BepInEx", "plugins", "DisabledMod", "DisabledMod.dll.disabled")));
    }

}
