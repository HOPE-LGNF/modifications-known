using ModificationsKnown.Core.Mods;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Tests;

public sealed class InstalledSourceStoreTests
{
    [Fact]
    public async Task RecordsAndReadsSourceByGameAndPackage()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sources.json");
        var store = new JsonInstalledSourceStore(path: path);
        var source = new InstalledSource(
            @"C:\Games\CU",
            "Sprite Replacer",
            "Gunsaw Genetics",
            "scavprototype",
            "324",
            "36039070580803",
            "2.0.0",
            "2377",
            DateTimeOffset.UtcNow);

        await store.RecordAsync([source]);

        var loaded = new JsonInstalledSourceStore(path: path)
            .Get(@"C:\Games\CU", "Sprite Replacer");

        Assert.NotNull(loaded);
        Assert.Equal("Gunsaw Genetics", loaded!.NexusName);
        Assert.Equal("324", loaded.GameScopedId);
        Assert.Equal("36039070580803", loaded.ModUid);
    }

    [Fact]
    public void UnknownPackageReturnsNull()
    {
        using var temp = new TempDirectory();
        var store = new JsonInstalledSourceStore(path: Path.Combine(temp.Path, "sources.json"));

        Assert.Null(store.Get(@"C:\Games\CU", "Not Installed"));
    }
}
