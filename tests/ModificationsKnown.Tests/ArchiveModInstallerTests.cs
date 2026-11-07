using System.IO.Compression;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Infrastructure.Installation;

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
}
