using ModificationsKnown.Core.Games;
using ModificationsKnown.Infrastructure.Games;

namespace ModificationsKnown.Tests;

public sealed class SteamGameLocatorTests
{
    [Fact]
    public async Task InspectDetectsGameAndBepInEx()
    {
        using var temp = new TempDirectory();
        var game = temp.CreateDirectory("CasualtiesUnknown");
        temp.CreateDirectory("CasualtiesUnknown", "CasualtiesUnknown_Data");
        temp.CreateDirectory("CasualtiesUnknown", "BepInEx", "plugins");
        temp.CreateFile("CasualtiesUnknown/CasualtiesUnknown.exe", "fake");
        temp.CreateFile("CasualtiesUnknown/winhttp.dll", "fake");
        temp.CreateFile("CasualtiesUnknown/.doorstop_version", "5");

        var settings = new FakeSettingsService();
        settings.Current.GamePath = game;

        var locator = new SteamGameLocator(settings);
        var installation = await locator.InspectAsync(game);

        Assert.NotNull(installation);
        Assert.Equal(KnownGameProfiles.Full.SteamAppId, installation!.Profile.SteamAppId);
        Assert.True(installation.HasBepInEx);
        Assert.True(installation.HasPluginsDirectory);
        Assert.Equal(Path.Combine(game, "CasualtiesUnknown.exe"), installation.ExecutablePath);
    }

    [Fact]
    public async Task InspectReturnsNullForNonGameDirectory()
    {
        using var temp = new TempDirectory();
        var locator = new SteamGameLocator(new FakeSettingsService());

        var installation = await locator.InspectAsync(temp.Path);

        Assert.Null(installation);
    }
}
