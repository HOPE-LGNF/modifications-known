using System.IO.Compression;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Infrastructure.BepInEx;
using ModificationsKnown.Infrastructure.Installation;

namespace ModificationsKnown.Tests;

public sealed class ManagedPackageLifecycleTests
{
    private static GameInstallation CreateInstallation(string gameDirectory) => new(
        KnownGameProfiles.Full,
        gameDirectory,
        null,
        HasBepInEx: true,
        HasPluginsDirectory: true);

    private static string CreateArchive(TempDirectory temp)
    {
        var archivePath = Path.Combine(temp.Path, "managed.zip");
        using var stream = File.Create(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        Write(archive, "BepInEx/plugins/ManagedMod/ManagedMod.dll", "fake dll");
        Write(archive, "BepInEx/plugins/ManagedMod/assets/data.txt", "managed data");
        return archivePath;
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    [Fact]
    public async Task InstallCreatesManifestAndScanSeesManagedPackage()
    {
        using var temp = new TempDirectory();
        var installation = CreateInstallation(temp.Path);
        Directory.CreateDirectory(installation.PluginsDirectory);
        var archive = CreateArchive(temp);

        var installer = new ArchiveModInstaller();
        var result = await installer.InstallArchiveAsync(installation, archive);

        Assert.True(result.Success, result.Message);
        var package = Path.Combine(installation.PluginsDirectory, "ManagedMod");
        Assert.True(File.Exists(Path.Combine(package, PackageManifest.DirectoryName, PackageManifest.FileName)));

        var manager = new BepInExModManager();
        var scanned = Assert.Single(await manager.ScanAsync(installation));
        Assert.True(scanned.IsManaged);
        Assert.Equal("已托管", scanned.ManagementLabel);
        Assert.Empty(scanned.MissingFiles);
        Assert.Empty(scanned.ModifiedFiles);
    }

    [Fact]
    public async Task DisabledManagedPackageKeepsFileStateClean()
    {
        using var temp = new TempDirectory();
        var installation = CreateInstallation(temp.Path);
        Directory.CreateDirectory(installation.PluginsDirectory);
        var archive = CreateArchive(temp);

        var installer = new ArchiveModInstaller();
        await installer.InstallArchiveAsync(installation, archive);

        var manager = new BepInExModManager();
        var scanned = Assert.Single(await manager.ScanAsync(installation));
        var disabled = await manager.SetEnabledAsync(installation, scanned, false);
        Assert.True(disabled.Success, disabled.Message);

        var after = Assert.Single(await manager.ScanAsync(installation));
        Assert.False(after.IsEnabled);
        Assert.Empty(after.MissingFiles);
        Assert.Empty(after.UnexpectedFiles);
    }

    [Fact]
    public async Task UninstallRemovesOnlyUnchangedManagedFiles()
    {
        using var temp = new TempDirectory();
        var installation = CreateInstallation(temp.Path);
        Directory.CreateDirectory(installation.PluginsDirectory);
        var archive = CreateArchive(temp);
        var installer = new ArchiveModInstaller();
        await installer.InstallArchiveAsync(installation, archive);

        var package = Path.Combine(installation.PluginsDirectory, "ManagedMod");
        var managedDll = Path.Combine(package, "ManagedMod.dll");
        var modifiedAsset = Path.Combine(package, "assets", "data.txt");
        var userFile = Path.Combine(package, "user-note.txt");
        File.WriteAllText(modifiedAsset, "user changed this");
        File.WriteAllText(userFile, "user added this");

        var manager = new BepInExModManager();
        var scanned = Assert.Single(await manager.ScanAsync(installation));
        Assert.Single(scanned.ModifiedFiles);
        Assert.Single(scanned.UnexpectedFiles);

        var result = await manager.UninstallManagedAsync(installation, scanned);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(managedDll));
        Assert.True(File.Exists(modifiedAsset));
        Assert.True(File.Exists(userFile));
        Assert.False(File.Exists(Path.Combine(package, PackageManifest.DirectoryName, PackageManifest.FileName)));
    }
}
