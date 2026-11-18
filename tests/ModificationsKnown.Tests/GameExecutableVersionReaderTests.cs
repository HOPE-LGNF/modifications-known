using ModificationsKnown.Core.Games;
using ModificationsKnown.Infrastructure.Games;

namespace ModificationsKnown.Tests;

/// <summary>Game version detection lives in Infrastructure so the view model
/// only consumes a string. Every failure path yields null (unknown version).</summary>
public sealed class GameExecutableVersionReaderTests
{
    [Fact]
    public void ReadVersion_ReturnsTheFileVersion()
    {
        using var temp = new TempDirectory();
        var executable = WriteExecutable(temp);
        var installation = Installation(executable);
        var reader = new GameExecutableVersionReader(path =>
        {
            Assert.Equal(executable, path);
            return "1.2.3.4";
        });

        Assert.Equal("1.2.3.4", reader.ReadVersion(installation));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadVersion_WithoutAPath_IsUnknown(string? path)
    {
        var reader = new GameExecutableVersionReader(_ => "1.2.3.4");

        Assert.Null(reader.ReadVersion(path));
    }

    [Fact]
    public void ReadVersion_WhenTheFileDoesNotExist_IsUnknown()
    {
        using var temp = new TempDirectory();
        var called = false;
        var reader = new GameExecutableVersionReader(_ =>
        {
            called = true;
            return "1.2.3.4";
        });

        var version = reader.ReadVersion(Path.Combine(temp.Path, "missing.exe"));

        Assert.Null(version);
        Assert.False(called);
    }

    [Fact]
    public void ReadVersion_WhenTheQueryFails_IsUnknown()
    {
        using var temp = new TempDirectory();
        var reader = new GameExecutableVersionReader(_ => throw new IOException("locked"));

        Assert.Null(reader.ReadVersion(WriteExecutable(temp)));
    }

    [Fact]
    public void ReadVersion_WhenTheFileHasNoVersion_IsUnknown()
    {
        using var temp = new TempDirectory();
        var reader = new GameExecutableVersionReader(_ => "  ");

        Assert.Null(reader.ReadVersion(WriteExecutable(temp)));
    }

    [Fact]
    public void ReadVersion_WithoutAnInjectedQuery_UsesTheRealFileInfo()
    {
        using var temp = new TempDirectory();
        var reader = new GameExecutableVersionReader();

        // A text file has no version resource; the point is that the default
        // path is exercised and cannot throw.
        Assert.Null(reader.ReadVersion(WriteExecutable(temp)));
    }

    private static string WriteExecutable(TempDirectory temp)
    {
        var path = Path.Combine(temp.Path, "CasualtiesUnknown.exe");
        File.WriteAllText(path, "not a real executable");
        return path;
    }

    private static GameInstallation Installation(string executable) =>
        new(KnownGameProfiles.Full, Path.GetDirectoryName(executable)!, executable, false, false);
}
