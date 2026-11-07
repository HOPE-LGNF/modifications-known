using ModificationsKnown.Core.Common;

namespace ModificationsKnown.Tests;

public sealed class PathSafetyTests
{
    [Theory]
    [InlineData("BepInEx/plugins/mod.dll", true)]
    [InlineData("BepInEx/plugins/sub/mod.dll", true)]
    [InlineData("../evil.dll", false)]
    [InlineData("BepInEx/../../evil.dll", false)]
    [InlineData("C:\\Windows\\evil.dll", false)]
    [InlineData("/etc/evil.dll", false)]
    public void IsSafeRelativePath_RejectsTraversalAndRootedPaths(string path, bool expected)
    {
        Assert.Equal(expected, PathSafety.IsSafeRelativePath(path));
    }

    [Fact]
    public void IsWithinDirectory_AcceptsChildrenOnly()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "root");
        var child = Path.Combine(root, "child", "file.txt");
        var outside = Path.Combine(temp.Path, "outside.txt");

        Assert.True(PathSafety.IsWithinDirectory(child, root));
        Assert.False(PathSafety.IsWithinDirectory(outside, root));
    }

    [Theory]
    [InlineData("My Mod", "My Mod")]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("   ", "unknown")]
    public void GetSafeFileName_RemovesInvalidCharacters(string input, string expected)
    {
        Assert.Equal(expected, PathSafety.GetSafeFileName(input));
    }
}
