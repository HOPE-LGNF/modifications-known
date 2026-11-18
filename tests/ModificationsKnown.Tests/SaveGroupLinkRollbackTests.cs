using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

/// <summary>
/// Failure paths that decide whether a group may be deleted. A switch whose
/// restore could not be confirmed must never lead to deleting a directory the
/// live junction might still reference; imported copies of player saves are
/// kept even when the operation fails.
/// </summary>
public sealed class SaveGroupLinkRollbackTests
{
    [Fact]
    public async Task ImportLive_WhenTheCatalogWriteFails_RestoresTheOldLinkAndKeepsTheCopy()
    {
        using var context = new SaveGroupTestContext();
        var first = await PrepareLiveImportAsync(context);
        context.WriteLiveFile("slot_3.sv", "live-save");

        // A directory where the index file should be: every write fails.
        var badCatalog = context.Temp.CreateDirectory("catalog-as-directory");
        var service = context.BuildService(catalogPath: badCatalog);

        var result = await service.ImportLiveIntoNewGroupAsync(context.Installation);

        Assert.False(result.Success);
        // A path that cannot hold the index makes the entry lookup fail; that is
        // the "catalog entry missing" branch, not a write exception.
        Assert.Contains("缺少该实例的记录", result.Message);
        Assert.Contains("已恢复原链接", result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);

        var group = Assert.IsType<SaveGroup>(result.Group);
        Assert.True(Directory.Exists(group.DirectoryPath));
        Assert.Equal("live-save", File.ReadAllText(Path.Combine(group.DirectoryPath, "slot_3.sv")));
    }

    [Fact]
    public async Task ImportLive_WhenTheIndexCannotBeWritten_RestoresTheOldLinkAndKeepsTheCopy()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var context = new SaveGroupTestContext();
        var first = await PrepareLiveImportAsync(context);
        context.WriteLiveFile("slot_3.sv", "live-save");

        File.SetAttributes(context.CatalogPath, FileAttributes.ReadOnly);
        try
        {
            var result = await context.Service.ImportLiveIntoNewGroupAsync(context.Installation);

            Assert.False(result.Success);
            Assert.Contains("保存存档索引失败", result.Message);
            Assert.Contains("已恢复原链接", result.Message);
            Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
            Assert.Equal(first.DirectoryPath, target);

            var group = Assert.IsType<SaveGroup>(result.Group);
            Assert.Equal("live-save", File.ReadAllText(Path.Combine(group.DirectoryPath, "slot_3.sv")));
        }
        finally
        {
            File.SetAttributes(context.CatalogPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task ImportLive_WhenTheRestoreAlsoFails_KeepsTheBackupAndReportsThePartialState()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var context = new SaveGroupTestContext();
        await PrepareInstanceWithGroupsAsync(context);

        // Unmanaged saves in a real folder, not behind the junction.
        Assert.True(context.Linker.RemoveJunction(context.Live, out _));
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "slot_9.sv"), "from live");

        File.SetAttributes(context.CatalogPath, FileAttributes.ReadOnly);
        // The junction the import created cannot be removed, so the original
        // directory cannot be put back.
        context.Linker.FailRemove = true;
        try
        {
            var result = await context.Service.ImportLiveIntoNewGroupAsync(context.Installation);

            Assert.False(result.Success);
            Assert.Contains("未能恢复原存档状态", result.Message);
            Assert.Contains(context.Live, result.Message);

            var backup = Assert.Single(Directory.GetDirectories(context.BackupsRoot));
            Assert.Equal("from live", File.ReadAllText(Path.Combine(backup, "slot_9.sv")));

            var group = Assert.IsType<SaveGroup>(result.Group);
            Assert.Equal("from live", File.ReadAllText(Path.Combine(group.DirectoryPath, "slot_9.sv")));
        }
        finally
        {
            context.Linker.FailRemove = false;
            File.SetAttributes(context.CatalogPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task ImportLive_WhenTheSwitchFails_KeepsTheImportedCopy()
    {
        using var context = new SaveGroupTestContext();
        await PrepareLiveImportAsync(context);
        context.WriteLiveFile("slot_4.sv", "live-save");

        // The switch and its rollback both fail: the live state is unknown.
        context.Linker.FailNextCreate = 2;

        var result = await context.Service.ImportLiveIntoNewGroupAsync(context.Installation);

        Assert.False(result.Success);
        Assert.Contains("已保留", result.Message);
        var group = Assert.IsType<SaveGroup>(result.Group);
        Assert.Equal("live-save", File.ReadAllText(Path.Combine(group.DirectoryPath, "slot_4.sv")));
    }

    [Fact]
    public async Task EnsureActiveGroup_WhenTheFirstSwitchFails_RollsBackTheEmptyGroup()
    {
        using var context = new SaveGroupTestContext();
        context.Linker.FailNextCreate = 1;

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.Equal(SaveGroupActivationState.Failed, result.State);
        Assert.Contains("已回滚", result.Message);
        Assert.Empty(context.Service.GetGroups(context.Installation));
        Assert.Empty(Directory.GetDirectories(context.InstallationDirectory));
        Assert.False(context.Linker.TryGetTarget(context.Live, out _));
    }

    [Fact]
    public async Task EnsureActiveGroup_WhenTheFirstSwitchAndItsRestoreFail_KeepsTheNewGroup()
    {
        using var context = new SaveGroupTestContext();
        await ActivateOtherInstallationAsync(context);

        context.Linker.FailNextCreate = 2;

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.Equal(SaveGroupActivationState.Failed, result.State);
        Assert.Contains("保留", result.Message);
        var group = Assert.Single(context.Service.GetGroups(context.Installation));
        Assert.True(Directory.Exists(group.DirectoryPath));
    }

    [Fact]
    public async Task CreateEmpty_WhenTheFirstSwitchFails_RollsBackTheEmptyGroup()
    {
        using var context = new SaveGroupTestContext();
        context.CreateLiveDirectory();
        context.Linker.FailNextCreate = 1;

        var result = await context.Service.CreateEmptyAsync(context.Installation, "第一个");

        Assert.False(result.Success);
        Assert.Contains("已回滚", result.Message);
        Assert.Empty(context.Service.GetGroups(context.Installation));
        // The empty folder the switch removed is put back: "unchanged" must be
        // literally true, not just "no data was lost".
        Assert.True(Directory.Exists(context.Live));
        Assert.Empty(Directory.GetFileSystemEntries(context.Live));
    }

    [Fact]
    public async Task CreateEmpty_WhenTheFirstSwitchAndItsRestoreFail_KeepsTheNewGroup()
    {
        using var context = new SaveGroupTestContext();
        // A dangling link inside this installation's own folder: the switch
        // removes it and then fails to recreate either target.
        var dead = Path.Combine(context.InstallationDirectory, "deadbeefdeadbeefdeadbeefdeadbeef");
        Directory.CreateDirectory(context.InstallationDirectory);
        context.CreateLiveDirectory();
        context.Linker.SetTarget(context.Live, dead);
        context.Linker.FailNextCreate = 2;

        var result = await context.Service.CreateEmptyAsync(context.Installation, "第一个");

        Assert.False(result.Success);
        Assert.Contains("保留", result.Message);
        var group = Assert.Single(context.Service.GetGroups(context.Installation));
        Assert.True(Directory.Exists(group.DirectoryPath));
    }

    [Fact]
    public async Task Initialize_WhenMigratingLegacyAndTheSwitchFails_KeepsTheMigratedCopy()
    {
        using var context = new SaveGroupTestContext();
        context.WriteLegacyFile("save.sv", "legacy-save");
        context.CreateLiveDirectory();
        context.Linker.CreateJunction(context.Live, context.InstallationDirectory, out _);
        context.Linker.FailNextCreate = 2;

        var init = await context.Service.InitializeAsync(context.Installation);

        Assert.Equal(SaveGroupInitializationState.Failed, init.State);
        Assert.Contains("迁移副本保留", init.Message);
        var group = Assert.Single(context.Service.GetGroups(context.Installation));
        Assert.Equal("legacy-save", File.ReadAllText(Path.Combine(group.DirectoryPath, "save.sv")));
        Assert.Equal("legacy-save", File.ReadAllText(Path.Combine(context.InstallationDirectory, "save.sv")));
    }

    [Fact]
    public async Task ImportLive_WithAnInvalidName_UsesTheSharedSanitizedPolicy()
    {
        using var context = new SaveGroupTestContext();
        await PrepareInstanceWithGroupsAsync(context);
        Assert.True(context.Linker.RemoveJunction(context.Live, out _));
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "slot_2.sv"), "live");

        var result = await context.Service.ImportLiveIntoNewGroupAsync(context.Installation, "a/b:c");

        Assert.True(result.Success, result.Message);
        Assert.Equal("abc", result.Group?.Name);
        Assert.Null(SaveGroupFormat.ValidateName(result.Group!.Name));
    }

    /// <summary>Two groups with the first one live, so an import takes the
    /// linked branch and has a previous target to restore.</summary>
    private static async Task<SaveGroup> PrepareLiveImportAsync(SaveGroupTestContext context)
    {
        var first = await PrepareInstanceWithGroupsAsync(context);
        var applied = await context.Service.ApplyCurrentAsync(context.Installation, first.Id);
        Assert.True(applied.Success, applied.Message);
        return first;
    }

    private static async Task<SaveGroup> PrepareInstanceWithGroupsAsync(SaveGroupTestContext context)
    {
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);
        var first = init.Groups.Single();

        var created = await context.Service.CreateEmptyAsync(context.Installation, "存档组 2");
        Assert.True(created.Success, created.Message);
        return first;
    }

    private static async Task ActivateOtherInstallationAsync(SaveGroupTestContext context)
    {
        var otherDirectory = context.Temp.CreateDirectory("other-game");
        var other = new GameInstallation(KnownGameProfiles.Demo, otherDirectory, null, false, false);
        var activated = await context.BuildService().EnsureActiveGroupAsync(other);
        Assert.True(activated.Success, activated.Message);
    }
}
