using System.Text.Json;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Core.Updates;

namespace ModificationsKnown.Tests;

public sealed class UpdateChannelSettingsTests
{
    [Fact]
    public void MissingFieldDefaultsToStable()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal(AppUpdateChannel.Stable, settings.UpdateChannel);
    }

    [Fact]
    public void PreviewRoundTrips()
    {
        var settings = new AppSettings { UpdateChannel = AppUpdateChannel.Preview };

        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"UpdateChannel\":\"preview\"", json);

        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(AppUpdateChannel.Preview, restored.UpdateChannel);
    }

    [Fact]
    public void StableSerializesAsLowercaseStable()
    {
        var json = JsonSerializer.Serialize(new AppSettings());
        Assert.Contains("\"UpdateChannel\":\"stable\"", json);
    }

    [Theory]
    [InlineData("{\"UpdateChannel\":\"nightly\"}")]
    [InlineData("{\"UpdateChannel\":null}")]
    [InlineData("{\"UpdateChannel\":7}")]
    [InlineData("{\"UpdateChannel\":true}")]
    [InlineData("{\"UpdateChannel\":{}}")]
    public void UnknownOrWrongTypedValueFallsBackToStable(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(AppUpdateChannel.Stable, settings.UpdateChannel);
    }

    [Fact]
    public void UnknownChannelDoesNotDiscardOtherSettings()
    {
        const string json = """
            {
              "NexusApiKey": "keep-me",
              "GamePath": "D:\\Games\\CU",
              "UpdateChannel": "nightly"
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(AppUpdateChannel.Stable, settings.UpdateChannel);
        Assert.Equal("keep-me", settings.NexusApiKey);
        Assert.Equal("D:\\Games\\CU", settings.GamePath);
    }

    [Theory]
    [InlineData("\"preview\"", AppUpdateChannel.Preview)]
    [InlineData("\"Preview\"", AppUpdateChannel.Preview)]
    [InlineData("\"PREVIEW\"", AppUpdateChannel.Preview)]
    [InlineData("\"stable\"", AppUpdateChannel.Stable)]
    public void ChannelNamesAreCaseInsensitive(string literal, AppUpdateChannel expected)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>($"{{\"UpdateChannel\":{literal}}}")!;
        Assert.Equal(expected, settings.UpdateChannel);
    }
}
