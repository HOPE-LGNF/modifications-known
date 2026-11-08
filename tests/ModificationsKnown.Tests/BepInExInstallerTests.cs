using System.IO.Compression;
using ModificationsKnown.Infrastructure.Installation;

namespace ModificationsKnown.Tests;

public sealed class BepInExInstallerTests
{
    [Fact]
    public async Task InstallsBepInExLayoutFromZip()
    {
        using var temp = new TempDirectory();
        var game = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(game);
        var archive = Path.Combine(temp.Path, "bepinex.zip");

        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "winhttp.dll", "loader");
            Write(zip, "BepInEx/core/BepInEx.dll", "core");
            Write(zip, "BepInEx/plugins/.keep", "");
        }

        var installer = new BepInExInstaller();
        var result = await installer.InstallAsync(game, archive);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(game, "winhttp.dll")));
        Assert.True(File.Exists(Path.Combine(game, "BepInEx", "core", "BepInEx.dll")));
        Assert.True(Directory.Exists(Path.Combine(game, "BepInEx", "plugins")));
    }

    [Fact]
    public async Task RefusesToOverwriteExistingLoader()
    {
        using var temp = new TempDirectory();
        var game = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "BepInEx"));
        File.WriteAllText(Path.Combine(game, "winhttp.dll"), "existing");
        var archive = Path.Combine(temp.Path, "bepinex.zip");
        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "winhttp.dll", "new");
            Write(zip, "BepInEx/core/BepInEx.dll", "core");
        }

        var installer = new BepInExInstaller();
        var result = await installer.InstallAsync(game, archive);

        Assert.False(result.Success);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(game, "winhttp.dll")));
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
