using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

public sealed class SaveGroupServiceTests
{
    [Fact]
    public void GetInstallationKey_IsStableAndIgnoresPathCase()
    {
        var upper = SaveGroupFormat.GetInstallationKey(@"C:\Games\Casualties");
        var lower = SaveGroupFormat.GetInstallationKey(@"c:\games\casualties\");

        Assert.Equal(upper, lower);
        Assert.Equal(16, upper.Length);
    }

    [Fact]
    public async Task LegacyProfile_IsMigratedToDefaultGroup_AndSourceIsKept()
    {
        using var context = new SaveGroupTestContext();
        context.WriteLegacyFile("save.sv", "save-data");
        context.WriteLegacyFile(Path.Combine("slot1", "save.sv"), "slot-one");
        context.WriteLegacyFile(SaveGroupFormat.LegacyProfileMarkerFileName, @"C:\Games\CU");
        context.CreateLiveDirectory();
        context.Linker.CreateJunction(context.Live, context.InstallationDirectory, out _);

        var result = await context.Service.InitializeAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        var group = Assert.Single(result.Groups);
        Assert.Equal("默认", group.Name);
        Assert.True(group.IsDefault);
        Assert.Equal(group.Id, result.NextLaunchGroupId);

        Assert.Equal("save-data", File.ReadAllText(Path.Combine(group.DirectoryPath, "save.sv")));
        Assert.Equal("slot-one", File.ReadAllText(Path.Combine(group.DirectoryPath, "slot1", "save.sv")));
        Assert.False(File.Exists(Path.Combine(group.DirectoryPath, SaveGroupFormat.LegacyProfileMarkerFileName)));
        Assert.True(SaveGroupManifestStore.TryRead(group.DirectoryPath, out var manifest));
        Assert.Equal(context.InstallationKey, manifest.InstallationKey);

        // The legacy profile remains untouched as the fallback copy.
        Assert.Equal("save-data", File.ReadAllText(Path.Combine(context.InstallationDirectory, "save.sv")));
    }

    [Fact]
    public async Task LegacyMigration_WhenCopyFails_LeavesCatalogAndLegacyProfileUntouched()
    {
        using var context = new SaveGroupTestContext();
        var legacyFile = context.WriteLegacyFile("save.sv", "locked-save");
        context.CreateLiveDirectory();
        context.Linker.CreateJunction(context.Live, context.InstallationDirectory, out _);

        await using (new FileStream(legacyFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await context.Service.InitializeAsync(context.Installation);

            Assert.Equal(SaveGroupInitializationState.Failed, result.State);
            Assert.Empty(result.Groups);
        }

        Assert.Equal("locked-save", File.ReadAllText(legacyFile));
        Assert.Empty(Directory.Exists(context.InstallationDirectory)
            ? Directory.EnumerateDirectories(context.InstallationDirectory)
            : []);
        Assert.Empty(context.Service.GetGroups(context.Installation));
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(context.InstallationDirectory, target);
    }

    [Fact]
    public async Task UnmanagedSaves_WithoutChoice_AskAndChangeNothing()
    {
        using var context = new SaveGroupTestContext();
        context.CreateLiveDirectory();
        context.WriteLiveFile("save.sv", "player-save");

        var result = await context.Service.InitializeAsync(context.Installation);

        Assert.Equal(SaveGroupInitializationState.NeedsUnmanagedChoice, result.State);
        Assert.Equal(1, result.UnmanagedFileCount);
        Assert.Equal("player-save", File.ReadAllText(Path.Combine(context.Live, "save.sv")));
        Assert.Empty(context.Service.GetGroups(context.Installation));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
    }

    [Fact]
    public async Task UnmanagedSaves_Import_CreatesGroupLinksItAndBacksUpOriginal()
    {
        using var context = new SaveGroupTestContext();
        context.CreateLiveDirectory();
        context.WriteLiveFile("save.sv", "player-save");
        context.WriteLiveFile(Path.Combine("slot2", "save.sv"), "slot-two");

        var result = await context.Service.InitializeAsync(
            context.Installation,
            UnmanagedSaveAction.Import);

        Assert.True(result.Success, result.Message);
        var group = Assert.Single(result.Groups);
        Assert.Equal("player-save", File.ReadAllText(Path.Combine(group.DirectoryPath, "save.sv")));
        Assert.Equal("slot-two", File.ReadAllText(Path.Combine(group.DirectoryPath, "slot2", "save.sv")));

        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);

        var backup = Assert.Single(Directory.EnumerateDirectories(context.BackupsRoot));
        Assert.Equal("player-save", File.ReadAllText(Path.Combine(backup, "save.sv")));
    }

    [Fact]
    public async Task UnmanagedSaves_CreateEmpty_KeepsOriginalAsBackupAndLinksEmptyGroup()
    {
        using var context = new SaveGroupTestContext();
        context.CreateLiveDirectory();
        context.WriteLiveFile("save.sv", "player-save");

        var result = await context.Service.InitializeAsync(
            context.Installation,
            UnmanagedSaveAction.CreateEmpty);

        Assert.True(result.Success, result.Message);
        var group = Assert.Single(result.Groups);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(group.DirectoryPath, "*", SearchOption.AllDirectories),
            path => !SaveGroupFormat.IsInternalFile(Path.GetFileName(path)));

        var backup = Assert.Single(Directory.EnumerateDirectories(context.BackupsRoot));
        Assert.Equal("player-save", File.ReadAllText(Path.Combine(backup, "save.sv")));
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);
    }

    [Fact]
    public async Task EmptyLiveDirectory_CreatesSelectedEmptyDefaultGroup()
    {
        using var context = new SaveGroupTestContext();
        context.CreateLiveDirectory();

        var result = await context.Service.InitializeAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        var group = Assert.Single(result.Groups);
        Assert.Equal("默认", group.Name);
        Assert.Equal(group.Id, result.NextLaunchGroupId);
        Assert.True(Directory.Exists(group.DirectoryPath));
        // 首个组在初始化时就成为实际 Current，不再等到下次启动。
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(group.DirectoryPath, target);
    }

    [Fact]
    public async Task PrepareLaunch_CreatesLinkToSelectedGroup()
    {
        using var context = new SaveGroupTestContext();
        var init = await context.Service.InitializeAsync(context.Installation);

        var launch = await context.Service.PrepareLaunchAsync(context.Installation);

        Assert.True(launch.Success, launch.Message);
        Assert.False(launch.Switched);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(init.Groups[0].DirectoryPath, target);
    }

    [Fact]
    public async Task PrepareLaunch_RetargetsLink_AndKeepsOldTargetFiles()
    {
        using var context = new SaveGroupTestContext();
        context.WriteLegacyFile("save.sv", "legacy-save");
        context.CreateLiveDirectory();
        context.Linker.CreateJunction(context.Live, context.InstallationDirectory, out _);

        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);

        var launch = await context.Service.PrepareLaunchAsync(context.Installation);

        Assert.True(launch.Success, launch.Message);
        Assert.False(launch.Switched);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(init.Groups[0].DirectoryPath, target);
        Assert.Equal("legacy-save", File.ReadAllText(Path.Combine(context.InstallationDirectory, "save.sv")));
    }

    [Fact]
    public async Task PrepareLaunch_WhenSwitchFails_RestoresPreviousLinkAndBlocksLaunch()
    {
        using var context = new SaveGroupTestContext();
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);
        var first = Assert.Single(init.Groups);

        var second = await context.Service.CreateEmptyAsync(context.Installation, "第二组");
        Assert.True(second.Success, second.Message);
        Assert.True((await context.Service.SelectNextLaunchAsync(context.Installation, second.Group!.Id)).Success);

        context.Linker.FailNextCreate = 1;
        var launch = await context.Service.PrepareLaunchAsync(context.Installation);

        Assert.False(launch.Success);
        Assert.Contains("不会启动", launch.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
    }

    [Fact]
    public async Task ExternalLiveLink_IsRejectedWithoutChanges()
    {
        using var context = new SaveGroupTestContext();
        var external = context.Temp.CreateDirectory("external-saves");
        File.WriteAllText(Path.Combine(external, "save.sv"), "external");
        context.CreateLiveDirectory();
        context.Linker.CreateJunction(context.Live, external, out _);

        var result = await context.Service.InitializeAsync(context.Installation);

        Assert.Equal(SaveGroupInitializationState.Failed, result.State);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(external, target);
        Assert.Equal("external", File.ReadAllText(Path.Combine(external, "save.sv")));
        Assert.Empty(context.Service.GetGroups(context.Installation));
    }

    [Fact]
    public void CorruptManifest_IsSkippedAndFilesAreNotDeleted()
    {
        using var context = new SaveGroupTestContext();
        var groupId = Guid.NewGuid();
        var groupDirectory = Path.Combine(context.InstallationDirectory, groupId.ToString("N"));
        Directory.CreateDirectory(groupDirectory);
        File.WriteAllText(Path.Combine(groupDirectory, SaveGroupFormat.ManifestFileName), "{not-json");
        File.WriteAllText(Path.Combine(groupDirectory, "save.sv"), "keep-me");

        var groups = context.Service.GetGroups(context.Installation);

        Assert.Empty(groups);
        Assert.True(File.Exists(Path.Combine(groupDirectory, "save.sv")));
    }

    [Fact]
    public void CatalogIsRebuiltFromManifests_AfterIndexLoss()
    {
        using var context = new SaveGroupTestContext();
        var groupId = Guid.NewGuid();
        var groupDirectory = Path.Combine(context.InstallationDirectory, groupId.ToString("N"));
        Directory.CreateDirectory(groupDirectory);
        SaveGroupManifestStore.Write(groupDirectory, new SaveGroupManifest
        {
            GroupId = groupId,
            Name = "找回的组",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            InstallationKey = context.InstallationKey,
            IsDefault = true
        });
        File.WriteAllText(Path.Combine(groupDirectory, "save.sv"), "recovered");
        Assert.False(File.Exists(context.CatalogPath));

        var groups = context.Service.GetGroups(context.Installation);
        var next = context.Service.GetNextLaunchGroupId(context.Installation);

        var group = Assert.Single(groups);
        Assert.Equal(groupId, group.Id);
        Assert.Equal("找回的组", group.Name);
        Assert.Equal(groupId, next);
        Assert.True(File.Exists(context.CatalogPath));
    }

    [Fact]
    public async Task RunningGame_BlocksInitializationAndLaunch()
    {
        using var context = new SaveGroupTestContext();
        context.CreateLiveDirectory();
        context.Running = true;

        var init = await context.Service.InitializeAsync(context.Installation);
        var launch = await context.Service.PrepareLaunchAsync(context.Installation);

        Assert.Equal(SaveGroupInitializationState.Failed, init.State);
        Assert.Contains("正在运行", init.Message);
        Assert.False(launch.Success);
        Assert.Contains("正在运行", launch.Message);
        Assert.False(Directory.Exists(context.InstallationDirectory));
    }

    [Fact]
    public void Manifest_StoresInstallationKey_NotAbsoluteUserPath()
    {
        using var context = new SaveGroupTestContext();
        var groupId = Guid.NewGuid();
        var groupDirectory = Path.Combine(context.InstallationDirectory, groupId.ToString("N"));
        Directory.CreateDirectory(groupDirectory);
        SaveGroupManifestStore.Write(groupDirectory, new SaveGroupManifest
        {
            GroupId = groupId,
            Name = "组",
            CreatedAt = DateTimeOffset.UtcNow,
            InstallationKey = context.InstallationKey
        });

        var json = File.ReadAllText(Path.Combine(groupDirectory, SaveGroupFormat.ManifestFileName));

        Assert.DoesNotContain(context.Temp.Path, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(context.GameDirectory, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(context.InstallationKey, json, StringComparison.OrdinalIgnoreCase);
    }
}
