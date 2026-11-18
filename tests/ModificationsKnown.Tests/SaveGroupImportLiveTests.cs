using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

/// <summary>Adopting the live saves as a new group of an installation that
/// already has groups: copies, names 「导入的存档」, switches the link and keeps
/// a real folder as a backup.</summary>
public sealed class SaveGroupImportLiveTests
{
    [Fact]
    public async Task ImportLiveIntoNewGroup_CopiesCurrentSavesAndSwitches()
    {
        using var context = new SaveGroupTestContext();
        await PrepareInstanceWithGroupsAsync(context);
        // Unmanaged saves live in a real folder, not behind a junction.
        context.Linker.Targets.Clear();
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "slot_9.sv"), "from live");

        var result = await context.Service.ImportLiveIntoNewGroupAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        Assert.Equal("导入的存档", result.Group?.Name);
        Assert.True(File.Exists(Path.Combine(result.Group!.DirectoryPath, "slot_9.sv")));
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(result.Group.DirectoryPath, target);
        Assert.Equal(result.Group.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task ImportLiveIntoNewGroup_WhenNameIsTaken_GetsNumbered()
    {
        using var context = new SaveGroupTestContext();
        await PrepareInstanceWithGroupsAsync(context);
        context.Linker.Targets.Clear();
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "slot_1.sv"), "one");

        var firstImport = await context.Service.ImportLiveIntoNewGroupAsync(context.Installation);
        context.Linker.Targets.Clear();
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "slot_2.sv"), "two");
        var secondImport = await context.Service.ImportLiveIntoNewGroupAsync(context.Installation);

        Assert.True(firstImport.Success, firstImport.Message);
        Assert.True(secondImport.Success, secondImport.Message);
        Assert.Equal("导入的存档", firstImport.Group?.Name);
        Assert.Equal("导入的存档 2", secondImport.Group?.Name);
    }

    [Fact]
    public async Task ImportLiveIntoNewGroup_WhenLiveIsARealFolder_KeepsABackup()
    {
        using var context = new SaveGroupTestContext();
        await PrepareInstanceWithGroupsAsync(context);

        // Replace the junction with a real folder holding the player's saves.
        Assert.True(context.Linker.RemoveJunction(context.Live, out _));
        Directory.CreateDirectory(context.Live);
        File.WriteAllText(Path.Combine(context.Live, "save.sv"), "unmanaged");

        var result = await context.Service.ImportLiveIntoNewGroupAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(result.Group!.DirectoryPath, target);
        Assert.True(File.Exists(Path.Combine(result.Group.DirectoryPath, "save.sv")));

        var backup = Directory.GetDirectories(context.BackupsRoot).Single();
        Assert.True(File.Exists(Path.Combine(backup, "save.sv")));
    }

    [Fact]
    public async Task ImportLiveIntoNewGroup_WhenGameIsRunning_IsLocked()
    {
        using var context = new SaveGroupTestContext();
        var first = await PrepareInstanceWithGroupsAsync(context);
        context.Running = true;

        var result = await context.Service.ImportLiveIntoNewGroupAsync(context.Installation);

        Assert.False(result.Success);
        Assert.Contains("正在运行", result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
    }

    private static async Task<SaveGroup> PrepareInstanceWithGroupsAsync(SaveGroupTestContext context)
    {
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);
        var first = init.Groups.Single();

        var created = await context.Service.CreateEmptyAsync(context.Installation, "存档组 2");
        Assert.True(created.Success, created.Message);

        var applied = await context.Service.ApplyCurrentAsync(context.Installation, first.Id);
        Assert.True(applied.Success, applied.Message);
        return first;
    }
}
