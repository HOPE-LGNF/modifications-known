using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Tests;

public sealed class SaveGroupDeleteTests
{
    [Fact]
    public async Task Delete_NonCurrentGroup_SendsToRecycleBinAndRemovesIndex()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        await context.Service.ApplyCurrentAsync(context.Installation, second.Id);

        var result = await context.Service.DeleteGroupAsync(context.Installation, first.Id);

        Assert.True(result.Success, result.Message);
        Assert.Contains(first.DirectoryPath, context.RecycleBin.SentPaths);
        Assert.False(Directory.Exists(first.DirectoryPath));
        var remaining = Assert.Single(context.Service.GetGroups(context.Installation));
        Assert.Equal(second.Id, remaining.Id);
        Assert.Equal(second.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task Delete_InitialGroup_DoesNotPromoteAnotherGroup()
    {
        using var context = new SaveGroupTestContext();
        var (initial, second) = await CreateTwoGroupsAsync(context);
        Assert.True(initial.IsDefault);
        await context.Service.ApplyCurrentAsync(context.Installation, second.Id);

        var result = await context.Service.DeleteGroupAsync(context.Installation, initial.Id);

        Assert.True(result.Success, result.Message);
        var remaining = Assert.Single(context.Service.GetGroups(context.Installation));
        Assert.Equal(second.Id, remaining.Id);
        Assert.False(remaining.IsDefault);
        Assert.Equal(second.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task Delete_CurrentGroup_FailsAndKeepsEverything()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        await context.Service.ApplyCurrentAsync(context.Installation, second.Id);

        var result = await context.Service.DeleteGroupAsync(context.Installation, second.Id);

        Assert.False(result.Success);
        Assert.Contains("选定的存档组", result.Message);
        Assert.True(Directory.Exists(second.DirectoryPath));
        Assert.Equal(2, context.Service.GetGroups(context.Installation).Count);
        Assert.Empty(context.RecycleBin.SentPaths);
    }

    [Fact]
    public async Task Delete_LiveLinkTarget_FailsEvenIfSelectionPointsElsewhere()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        await context.Service.ApplyCurrentAsync(context.Installation, second.Id);
        context.Linker.CreateJunction(context.Live, first.DirectoryPath, out _);

        var result = await context.Service.DeleteGroupAsync(context.Installation, first.Id);

        Assert.False(result.Success);
        Assert.Contains("正在使用", result.Message);
        Assert.True(Directory.Exists(first.DirectoryPath));
        Assert.Empty(context.RecycleBin.SentPaths);
    }

    [Fact]
    public async Task Delete_LastGroup_Fails()
    {
        using var context = new SaveGroupTestContext();
        var init = await context.Service.InitializeAsync(context.Installation);
        var only = init.Groups.Single();

        var result = await context.Service.DeleteGroupAsync(context.Installation, only.Id);

        Assert.False(result.Success);
        Assert.Contains("至少保留一个", result.Message);
        Assert.True(Directory.Exists(only.DirectoryPath));
    }

    [Fact]
    public async Task Delete_WhenRecycleBinFails_KeepsDirectoryAndIndex()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        await context.Service.ApplyCurrentAsync(context.Installation, second.Id);
        context.RecycleBin.Fail = true;

        var result = await context.Service.DeleteGroupAsync(context.Installation, first.Id);

        Assert.False(result.Success);
        Assert.Contains("未做任何修改", result.Message);
        Assert.True(Directory.Exists(first.DirectoryPath));
        Assert.Equal(2, context.Service.GetGroups(context.Installation).Count);
        Assert.Equal(second.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task Delete_WhenGameIsRunning_FailsWithoutTouchingAnything()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        await context.Service.ApplyCurrentAsync(context.Installation, second.Id);
        context.Running = true;

        var result = await context.Service.DeleteGroupAsync(context.Installation, first.Id);

        Assert.False(result.Success);
        Assert.Contains("正在运行", result.Message);
        Assert.True(Directory.Exists(first.DirectoryPath));
        Assert.Empty(context.RecycleBin.SentPaths);
    }

    [Fact]
    public async Task Delete_WhenCatalogSaveFails_GroupIsInBinAndRefreshDropsIt()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        var thirdResult = await context.Service.CreateEmptyAsync(context.Installation, "存档组 3");
        Assert.True(thirdResult.Success, thirdResult.Message);
        var third = Assert.IsType<SaveGroup>(thirdResult.Group);
        await context.Service.ApplyCurrentAsync(context.Installation, third.Id);

        var badCatalog = context.Temp.CreateDirectory("delete-catalog-as-directory");
        var badService = context.BuildService(catalogPath: badCatalog);

        var result = await badService.DeleteGroupAsync(context.Installation, second.Id);

        Assert.False(result.Success);
        Assert.Contains("索引更新失败", result.Message);
        Assert.False(Directory.Exists(second.DirectoryPath));
        var remaining = context.Service.GetGroups(context.Installation);
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, group => group.Id == first.Id);
        Assert.Contains(remaining, group => group.Id == third.Id);
    }

    private static async Task<(SaveGroup First, SaveGroup Second)> CreateTwoGroupsAsync(
        SaveGroupTestContext context)
    {
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);
        var first = init.Groups.Single();

        var created = await context.Service.CreateEmptyAsync(context.Installation, "存档组 2");
        Assert.True(created.Success, created.Message);
        return (first, Assert.IsType<SaveGroup>(created.Group));
    }
}
