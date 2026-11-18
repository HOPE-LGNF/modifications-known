using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Tests;

/// <summary>Name validation and the cleanup used for suggestions must share one
/// character policy; otherwise a suggested name can be one the service refuses.</summary>
public sealed class SaveGroupNameTests
{
    public static TheoryData<string> InvalidCharacters =>
    [
        "<", ">", ":", "\"", "/", "\\", "|", "?", "*",
        // control characters are invalid even when the platform allows them
        "\u0001", "\0"
    ];

    [Theory]
    [MemberData(nameof(InvalidCharacters))]
    public void ValidateName_RejectsTheSharedInvalidCharacters(string character)
    {
        Assert.True(SaveGroupFormat.IsInvalidNameCharacter(character[0]));
        Assert.NotNull(SaveGroupFormat.ValidateName("组" + character + "名"));
    }

    [Theory]
    [MemberData(nameof(InvalidCharacters))]
    public void SanitizeName_RemovesExactlyTheCharactersValidationRejects(string character)
    {
        var sanitized = SaveGroupFormat.SanitizeName("组" + character + "名");

        Assert.Equal("组名", sanitized);
        Assert.Null(SaveGroupFormat.ValidateName(sanitized));
    }

    [Fact]
    public void SanitizeName_TruncatesToTheLimitValidateNameEnforces()
    {
        var overlong = new string('长', SaveGroupFormat.MaxNameLength + 10);

        var sanitized = SaveGroupFormat.SanitizeName(overlong);

        Assert.Equal(SaveGroupFormat.MaxNameLength, sanitized.Length);
        Assert.Null(SaveGroupFormat.ValidateName(sanitized));
        Assert.NotNull(SaveGroupFormat.ValidateName(overlong));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("  ")]
    public void SanitizeName_ReturnsEmptyForUnusableNames(string name)
    {
        Assert.Equal(string.Empty, SaveGroupFormat.SanitizeName(name));
        Assert.NotNull(SaveGroupFormat.ValidateName(SaveGroupFormat.SanitizeName(name)));
    }

    [Fact]
    public void ValidateName_AcceptsOrdinaryNamesAndKeepsTheLengthLimit()
    {
        Assert.Null(SaveGroupFormat.ValidateName("导入的存档 2"));
        Assert.Null(SaveGroupFormat.ValidateName(new string('a', SaveGroupFormat.MaxNameLength)));
        Assert.Contains("不能超过", SaveGroupFormat.ValidateName(new string('a', SaveGroupFormat.MaxNameLength + 1)));
    }
}
