using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

public sealed class SaveGroupApplyTests
{
    [Fact]
    public async Task ApplyCurrent_SwitchesLiveLinkAndPersistsSelection()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        context.Linker.CreateJunction(context.Live, first.DirectoryPath, out _);

        var result = await context.Service.ApplyCurrentAsync(context.Installation, second.Id);

        Assert.True(result.Success, result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(second.DirectoryPath, target);
        Assert.Equal(second.Id, context.Service.GetNextLaunchGroupId(context.Installation));
        Assert.Equal(second.Id, context.Service.GetLiveGroup(context.Installation)?.Id);
        Assert.True(Directory.Exists(first.DirectoryPath));
    }

    [Fact]
    public async Task ApplyCurrent_WhenAlreadyCurrent_IsNoOp()
    {
        using var context = new SaveGroupTestContext();
        var (first, _) = await CreateTwoGroupsAsync(context);
        context.Linker.CreateJunction(context.Live, first.DirectoryPath, out _);

        var result = await context.Service.ApplyCurrentAsync(context.Installation, first.Id);

        Assert.True(result.Success, result.Message);
        Assert.Contains("已经是当前存档", result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
    }

    [Fact]
    public async Task ApplyCurrent_WhenLinkFails_RestoresPreviousLinkAndKeepsSelection()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        context.Linker.CreateJunction(context.Live, first.DirectoryPath, out _);
        context.Linker.FailNextCreate = 1;

        var result = await context.Service.ApplyCurrentAsync(context.Installation, second.Id);

        Assert.False(result.Success);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
        Assert.Equal(first.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task ApplyCurrent_WhenCatalogSaveFails_RestoresPreviousLink()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        context.Linker.CreateJunction(context.Live, first.DirectoryPath, out _);

        var badCatalog = context.Temp.CreateDirectory("catalog-as-directory");
        var badService = context.BuildService(catalogPath: badCatalog);

        var result = await badService.ApplyCurrentAsync(context.Installation, second.Id);

        Assert.False(result.Success);
        Assert.Contains("恢复原链接", result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
    }

    [Fact]
    public async Task ApplyCurrent_WhenGameIsRunning_FailsWithoutChangingAnything()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        context.Linker.CreateJunction(context.Live, first.DirectoryPath, out _);
        context.Running = true;

        var result = await context.Service.ApplyCurrentAsync(context.Installation, second.Id);

        Assert.False(result.Success);
        Assert.Contains("正在运行", result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
        Assert.Equal(first.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task ApplyCurrent_RejectsExternalLiveLink()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        var external = context.Temp.CreateDirectory("external-saves");
        context.Linker.CreateJunction(context.Live, external, out _);

        var result = await context.Service.ApplyCurrentAsync(context.Installation, second.Id);

        Assert.False(result.Success);
        Assert.Contains("MK 之外", result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(external, target);
        Assert.Equal(first.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task ApplyCurrent_WithUnknownGroup_Fails()
    {
        using var context = new SaveGroupTestContext();
        await CreateTwoGroupsAsync(context);

        var result = await context.Service.ApplyCurrentAsync(context.Installation, Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Contains("不存在", result.Message);
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
