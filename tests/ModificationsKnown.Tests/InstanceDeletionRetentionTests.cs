using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Instances;
using ModificationsKnown.Infrastructure.Saves;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.Tests;

/// <summary>
/// Ticket 05: removing an instance record must never copy, move, delete or
/// re-link a save group. Every root here is a throwaway temp directory; the
/// player's real AppData/LocalLow is never touched.
/// </summary>
public sealed class InstanceDeletionRetentionTests
{
    [Fact]
    public async Task RemovingInstanceRecord_KeepsGroupDirectoriesCatalogAndSelection()
    {
        using var context = new InstanceDeletionTestContext();
        var (_, otherGroup) = context.CreateTwoGroupsWithSelection();

        var groupsBefore = SnapshotFiles(context.InstallationDirectory);
        var catalogBefore = File.ReadAllBytes(context.CatalogPath);
        var nextBefore = context.SaveGroups.GetNextLaunchGroupId(context.Installation);
        Assert.Equal(otherGroup, nextBefore);

        var service = context.BuildService();
        var displayed = service.GetGroups(context.Installation);
        Assert.Equal(2, displayed.Count);

        var report = await service.ExecuteAsync(
            context.Installation,
            new InstanceDeletionPlan(DeleteInstanceFolder: false));

        Assert.False(report.Aborted);
        Assert.True(report.Success, report.Summary);

        // The MK record is gone, the game folder stays.
        Assert.Null(context.Instances.Get(context.GameDirectory));
        Assert.True(Directory.Exists(context.GameDirectory));

        // Nothing under the profiles root changed, byte for byte.
        AssertSameSnapshot(groupsBefore, SnapshotFiles(context.InstallationDirectory));
        Assert.Equal(catalogBefore, File.ReadAllBytes(context.CatalogPath));

        // The installation entry and its next-launch choice survive in the index.
        var entry = LoadEntry(context);
        Assert.Equal(2, entry.Groups.Count);
        Assert.Equal(otherGroup, entry.NextLaunchGroupId);
        Assert.Equal(otherGroup, context.SaveGroups.GetNextLaunchGroupId(context.Installation));
        Assert.Equal(2, context.SaveGroups.GetGroups(context.Installation).Count);
    }

    [Fact]
    public async Task DeletingGameFolder_KeepsGroupsAndRemovesOnlyTheGameFolder()
    {
        using var context = new InstanceDeletionTestContext();
        var (_, otherGroup) = context.CreateTwoGroupsWithSelection();

        var groupsBefore = SnapshotFiles(context.InstallationDirectory);
        var catalogBefore = File.ReadAllBytes(context.CatalogPath);

        var service = context.BuildService();
        var report = await service.ExecuteAsync(
            context.Installation,
            new InstanceDeletionPlan(DeleteInstanceFolder: true));

        Assert.False(report.Aborted);
        Assert.True(report.Success, report.Summary);
        Assert.False(Directory.Exists(context.GameDirectory));
        Assert.Null(context.Instances.Get(context.GameDirectory));

        AssertSameSnapshot(groupsBefore, SnapshotFiles(context.InstallationDirectory));
        Assert.Equal(catalogBefore, File.ReadAllBytes(context.CatalogPath));
        Assert.Equal(otherGroup, context.SaveGroups.GetNextLaunchGroupId(context.Installation));
        Assert.Equal(2, context.SaveGroups.GetGroups(context.Installation).Count);
    }

    [Fact]
    public async Task DeletingGameFolder_WhenLocked_AbortsBeforeTouchingAnyRecord()
    {
        using var context = new InstanceDeletionTestContext();
        context.CreateTwoGroupsWithSelection();

        var groupsBefore = SnapshotFiles(context.InstallationDirectory);
        var catalogBefore = File.ReadAllBytes(context.CatalogPath);
        var locked = Path.Combine(context.GameDirectory, "locked.log");
        File.WriteAllText(locked, "in use");

        var service = context.BuildService();
        await using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = await service.ExecuteAsync(
                context.Installation,
                new InstanceDeletionPlan(DeleteInstanceFolder: true));

            Assert.True(report.Aborted);
            Assert.Contains(report.Steps, step => step.Name == "删除游戏安装目录" && !step.Success);
        }

        // A failed folder deletion must leave the record usable.
        Assert.NotNull(context.Instances.Get(context.GameDirectory));
        Assert.True(Directory.Exists(context.GameDirectory));
        AssertSameSnapshot(groupsBefore, SnapshotFiles(context.InstallationDirectory));
        Assert.Equal(catalogBefore, File.ReadAllBytes(context.CatalogPath));
    }

    [Fact]
    public async Task ReaddingTheSameGameDirectory_RecoversGroupsAndNextLaunchSelection()
    {
        using var context = new InstanceDeletionTestContext();
        var (defaultGroup, otherGroup) = context.CreateTwoGroupsWithSelection();

        var service = context.BuildService();
        var report = await service.ExecuteAsync(
            context.Installation,
            new InstanceDeletionPlan(DeleteInstanceFolder: false));
        Assert.True(report.Success, report.Summary);
        Assert.Empty(context.SaveGroups.GetGroups(new GameInstallation(
            KnownGameProfiles.Full,
            Path.Combine(context.Temp.Path, "other-install"),
            null,
            false,
            false)));

        // Re-adding the same path is a plain upsert: the groups were never gone.
        context.Instances.Upsert(context.Installation);

        var groups = context.SaveGroups.GetGroups(context.Installation);
        Assert.Equal(
            new[] { defaultGroup, otherGroup }.OrderBy(id => id).ToArray(),
            groups.Select(group => group.Id).OrderBy(id => id).ToArray());
        Assert.Equal(otherGroup, context.SaveGroups.GetNextLaunchGroupId(context.Installation));
        Assert.Equal("默认", groups.Single(group => group.Id == defaultGroup).Name);
        Assert.Equal("周末存档", groups.Single(group => group.Id == otherGroup).Name);
        Assert.True(File.Exists(Path.Combine(
            groups.Single(group => group.Id == otherGroup).DirectoryPath, "save.sv")));
    }

    [Fact]
    public async Task MetadataFailures_AreReportedPerStep_AndNeverTouchSaveGroups()
    {
        using var context = new InstanceDeletionTestContext();
        context.CreateTwoGroupsWithSelection();

        var groupsBefore = SnapshotFiles(context.InstallationDirectory);
        var catalogBefore = File.ReadAllBytes(context.CatalogPath);

        var service = context.BuildService(
            removeModGroups: _ => throw new InvalidOperationException("mod-groups.json 被占用"),
            removeManualMatches: _ => throw new IOException("Nexus 手动关联被占用"));

        var report = await service.ExecuteAsync(
            context.Installation,
            new InstanceDeletionPlan(DeleteInstanceFolder: false));

        Assert.False(report.Aborted);
        Assert.False(report.Success);
        Assert.Contains(report.Steps, step => step.Name == "移除 Mod 组快照" && !step.Success);
        Assert.Contains(report.Steps, step => step.Name == "移除 Nexus 手动关联" && !step.Success);
        Assert.Contains(report.Steps, step => step.Name == "移除版本记录" && step.Success);
        Assert.Contains(report.Steps, step => step.Name == "移除安装来源记录" && step.Success);

        // The record itself still goes away, and no save data is touched.
        Assert.Null(context.Instances.Get(context.GameDirectory));
        AssertSameSnapshot(groupsBefore, SnapshotFiles(context.InstallationDirectory));
        Assert.Equal(catalogBefore, File.ReadAllBytes(context.CatalogPath));
        Assert.Equal(2, context.SaveGroups.GetGroups(context.Installation).Count);
    }

    [Fact]
    public async Task RunningInstance_RefusesAndChangesNothing()
    {
        using var context = new InstanceDeletionTestContext();
        context.CreateTwoGroupsWithSelection();

        var groupsBefore = SnapshotFiles(context.InstallationDirectory);
        var catalogBefore = File.ReadAllBytes(context.CatalogPath);
        context.Running = true;

        var service = context.BuildService();
        var report = await service.ExecuteAsync(
            context.Installation,
            new InstanceDeletionPlan(DeleteInstanceFolder: true));

        Assert.True(report.Aborted);
        var step = Assert.Single(report.Steps);
        Assert.False(step.Success);
        Assert.Contains("正在运行", step.Message);

        Assert.NotNull(context.Instances.Get(context.GameDirectory));
        Assert.True(Directory.Exists(context.GameDirectory));
        AssertSameSnapshot(groupsBefore, SnapshotFiles(context.InstallationDirectory));
        Assert.Equal(catalogBefore, File.ReadAllBytes(context.CatalogPath));
    }

    private static SaveGroupInstallationEntry LoadEntry(InstanceDeletionTestContext context) =>
        new SaveGroupCatalogStore(context.CatalogPath).Load().Installations
            .Single(entry => string.Equals(
                entry.InstallationKey, context.InstallationKey, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, byte[]> SnapshotFiles(string root)
    {
        var snapshot = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return snapshot;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            snapshot[Path.GetRelativePath(root, file)] = File.ReadAllBytes(file);
        return snapshot;
    }

    private static void AssertSameSnapshot(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(
            expected.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
            actual.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray());

        foreach (var pair in expected)
            Assert.Equal(pair.Value, actual[pair.Key]);
    }
}

internal sealed class InstanceDeletionTestContext : IDisposable
{
    public InstanceDeletionTestContext()
    {
        Temp = new TempDirectory();
        ProfilesRoot = Temp.CreateDirectory("profiles");
        BackupsRoot = Temp.CreateDirectory("backups");
        StagingRoot = Temp.CreateDirectory("staging");
        Live = Path.Combine(Temp.Path, "LocalLow", "Orsoniks", "CasualtiesUnknown");
        CatalogPath = Path.Combine(Temp.Path, "save-groups.json");
        GameDirectory = Temp.CreateDirectory("game");
        File.WriteAllText(Path.Combine(GameDirectory, "CasualtiesUnknown.exe"), "exe");

        Installation = new GameInstallation(KnownGameProfiles.Full, GameDirectory, null, false, false);
        SaveGroups = new SaveGroupService(new SaveGroupOptions
        {
            ProfilesRoot = ProfilesRoot,
            CatalogPath = CatalogPath,
            BackupsRoot = BackupsRoot,
            StagingRoot = StagingRoot,
            LiveSaveDirectory = Live,
            Linker = Linker,
            IsGameRunning = _ => Running
        });

        Instances = new GameInstanceStore(Path.Combine(Temp.Path, "game-instances.json"));
        Sources = new JsonInstalledSourceStore(null, Path.Combine(Temp.Path, "installed-sources.json"));
        Instances.Upsert(Installation);
    }

    public TempDirectory Temp { get; }

    public string ProfilesRoot { get; }

    public string BackupsRoot { get; }

    public string StagingRoot { get; }

    public string Live { get; }

    public string CatalogPath { get; }

    public string GameDirectory { get; }

    public GameInstallation Installation { get; }

    public FakeSaveLinker Linker { get; } = new();

    public SaveGroupService SaveGroups { get; }

    public GameInstanceStore Instances { get; }

    public JsonInstalledSourceStore Sources { get; }

    public bool Running { get; set; }

    public string InstallationKey => SaveGroupFormat.GetInstallationKey(GameDirectory);

    public string InstallationDirectory => Path.Combine(ProfilesRoot, InstallationKey);

    public InstanceDeletionService BuildService(
        Func<string, bool>? removeModGroups = null,
        Func<string, int>? removeManualMatches = null) =>
        new(
            Instances,
            removeModGroups ?? (_ => true),
            removeManualMatches ?? (_ => 0),
            Sources,
            SaveGroups,
            _ => Running);

    public (Guid DefaultGroup, Guid OtherGroup) CreateTwoGroupsWithSelection()
    {
        var defaultGroup = CreateGroup("默认", isDefault: true, DateTimeOffset.UtcNow.AddHours(-2));
        var otherGroup = CreateGroup("周末存档", isDefault: false, DateTimeOffset.UtcNow.AddHours(-1));

        SaveGroups.GetGroups(Installation);

        var store = new SaveGroupCatalogStore(CatalogPath);
        var catalog = store.Load();
        var entry = catalog.Installations.Single(candidate => string.Equals(
            candidate.InstallationKey, InstallationKey, StringComparison.OrdinalIgnoreCase));
        entry.NextLaunchGroupId = otherGroup;
        store.Save(catalog);

        Assert.Equal(otherGroup, SaveGroups.GetNextLaunchGroupId(Installation));
        Assert.Equal(2, SaveGroups.GetGroups(Installation).Count);
        return (defaultGroup, otherGroup);
    }

    public void Dispose() => Temp.Dispose();

    private Guid CreateGroup(string name, bool isDefault, DateTimeOffset createdAt)
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(InstallationDirectory, id.ToString("N"));
        Directory.CreateDirectory(directory);
        SaveGroupManifestStore.Write(directory, new SaveGroupManifest
        {
            SchemaVersion = SaveGroupFormat.ManifestSchemaVersion,
            GroupId = id,
            Name = name,
            CreatedAt = createdAt,
            InstallationKey = InstallationKey,
            IsDefault = isDefault
        });

        File.WriteAllText(Path.Combine(directory, "save.sv"), $"payload-{name}");
        Directory.CreateDirectory(Path.Combine(directory, "slot1"));
        File.WriteAllText(Path.Combine(directory, "slot1", "save.sv"), "slot-one");
        return id;
    }
}
