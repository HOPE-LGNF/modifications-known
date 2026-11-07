using System.IO.Compression;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Infrastructure.BepInEx;
using ModificationsKnown.Infrastructure.Installation;

namespace ModificationsKnown.Tests;

public sealed class InstallPlanTests
{
    private static GameInstallation CreateInstallation(string gameDirectory) => new(
        KnownGameProfiles.Full,
        gameDirectory,
        null,
        HasBepInEx: true,
        HasPluginsDirectory: true);

    private static string CreateArchive(TempDirectory temp, params (string Path, string Content)[] files)
    {
        var path = Path.Combine(temp.Path, "test.zip");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(file.Path);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(file.Content);
        }
        return path;
    }

    [Fact]
    public async Task NestedDllAndResourcesAreOnePackageWithManifest()
    {
        using var temp = new TempDirectory();
        var installation = CreateInstallation(temp.Path);
        Directory.CreateDirectory(installation.PluginsDirectory);
        var archive = CreateArchive(temp,
            ("BepInEx/plugins/Nested/plugins/Nested.dll", "fake dll"),
            ("BepInEx/plugins/Nested/assets/readme.txt", "asset"));

        var result = await new ArchiveModInstaller().InstallArchiveAsync(
            installation, archive, source: PackageSource.Nexus("Nested Mod", "scavprototype", "42", "uid", "7", "1.0.0"));

        Assert.True(result.Success, result.Message);
        var package = Path.Combine(installation.PluginsDirectory, "Nested");
        Assert.True(File.Exists(Path.Combine(package, "plugins", "Nested.dll")));
        Assert.True(File.Exists(Path.Combine(package, "assets", "readme.txt")));
        Assert.True(File.Exists(Path.Combine(package, PackageManifest.DirectoryName, PackageManifest.FileName)));
        Assert.False(Directory.Exists(Path.Combine(installation.PluginsDirectory, ".modifications-known", "staging")));

        var mod = Assert.Single(await new BepInExModManager().ScanAsync(installation));
        Assert.True(mod.IsManaged);
        Assert.Equal("Nested Mod", mod.Source?.NexusName);
        Assert.Equal("42", mod.Source?.GameScopedId);
    }

    [Fact]
    public async Task PartialArchiveInstallReportsConflictingPackage()
    {
        using var temp = new TempDirectory();
        var installation = CreateInstallation(temp.Path);
        Directory.CreateDirectory(installation.PluginsDirectory);
        Directory.CreateDirectory(Path.Combine(installation.PluginsDirectory, "AlreadyThere"));
        var archive = CreateArchive(temp,
            ("BepInEx/plugins/NewMod/NewMod.dll", "new"),
            ("BepInEx/plugins/AlreadyThere/AlreadyThere.dll", "existing"));

        var result = await new ArchiveModInstaller().InstallArchiveAsync(installation, archive);

        Assert.True(result.Success, result.Message);
        Assert.Contains(result.Packages, package => package.PackageName == "NewMod" && package.Success);
        Assert.Contains(result.Packages, package => package.PackageName == "AlreadyThere" && !package.Success);
        Assert.True(Directory.Exists(Path.Combine(installation.PluginsDirectory, "NewMod")));
        Assert.Contains("1 个失败", result.Message);
    }

    [Fact]
    public async Task RejectsLooseRootDllInPluginsLayout()
    {
        using var temp = new TempDirectory();
        var installation = CreateInstallation(temp.Path);
        Directory.CreateDirectory(installation.PluginsDirectory);
        var archive = CreateArchive(temp,
            ("BepInEx/plugins/Loose.dll", "loose"),
            ("BepInEx/plugins/Folder/Folder.dll", "folder"));

        var result = await new ArchiveModInstaller().InstallArchiveAsync(installation, archive);

        Assert.False(result.Success);
    }
}
