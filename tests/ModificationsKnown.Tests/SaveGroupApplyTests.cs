using ModificationsKnown.Core.Games;
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

    [Fact]
    public async Task ApplyCurrent_WhenRestoreAlsoFails_DoesNotClaimTheLinkWasRestored()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        context.Linker.CreateJunction(context.Live, first.DirectoryPath, out _);

        // 1st create fails (the switch), 2nd create fails (the rollback).
        context.Linker.FailNextCreate = 2;

        var result = await context.Service.ApplyCurrentAsync(context.Installation, second.Id);

        Assert.False(result.Success);
        Assert.DoesNotContain("已恢复原链接", result.Message);
        Assert.Contains("未恢复", result.Message);
        Assert.Contains(context.Live, result.Message);
        Assert.Contains(first.DirectoryPath, result.Message);
    }

    [Fact]
    public async Task Mutations_WhenRunningStateIsUnknown_FailClosedAndChangeNothing()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        context.Linker.CreateJunction(context.Live, first.DirectoryPath, out _);

        context.ProbeState = GameRunningState.Unknown;
        var apply = await context.Service.ApplyCurrentAsync(context.Installation, second.Id);
        var delete = await context.Service.DeleteGroupAsync(context.Installation, second.Id);
        var create = await context.Service.CreateEmptyAsync(context.Installation, "第三组");
        var rename = await context.Service.RenameAsync(context.Installation, second.Id, "改名");
        var clone = await context.Service.CloneAsync(context.Installation, first.Id, "副本");

        foreach (var message in new[] { apply.Message, delete.Message, create.Message, rename.Message, clone.Message })
            Assert.Contains("无法确认", message);

        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
        Assert.Equal(2, context.Service.GetGroups(context.Installation).Count);
        Assert.True(Directory.Exists(second.DirectoryPath));
        Assert.Empty(context.RecycleBin.SentPaths);
        Assert.Equal(first.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task ConcurrentApplyAndDeleteOfTheSameGroup_NeverRecyclesTheLiveTarget()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);

        var tasks = new List<Task>();
        for (var index = 0; index < 12; index++)
        {
            tasks.Add(Task.Run(() => context.Service.ApplyCurrentAsync(context.Installation, second.Id)));
            tasks.Add(Task.Run(() => context.Service.DeleteGroupAsync(context.Installation, second.Id)));
        }

        await Task.WhenAll(tasks);

        // Whichever side won, the live junction must still point at an existing group.
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.True(Directory.Exists(target));
        Assert.DoesNotContain(context.RecycleBin.SentPaths, path => PathEquals(path, target));
        Assert.Contains(
            context.Service.GetGroups(context.Installation),
            group => PathEquals(group.DirectoryPath, target));
        _ = first;
    }

    [Fact]
    public async Task FirstGroupOfAnotherInstall_IsCreatedButDoesNotStealTheLiveLink()
    {
        using var context = new SaveGroupTestContext();
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);
        var first = Assert.Single(init.Groups);

        var otherDirectory = context.Temp.CreateDirectory("game-2");
        var other = new GameInstallation(KnownGameProfiles.Full, otherDirectory, null, false, false);

        var created = await context.Service.CreateEmptyAsync(other, "另一安装的组");

        Assert.True(created.Success, created.Message);
        Assert.Contains("另一个安装", created.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);

        // An explicit Apply is still allowed to switch between installations.
        var applied = await context.Service.ApplyCurrentAsync(other, created.Group!.Id);
        Assert.True(applied.Success, applied.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var switched));
        Assert.Equal(created.Group.DirectoryPath, switched);
    }

    [Fact]
    public async Task Delete_WhenTheLiveLinkExistsButCannotBeRead_FailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);

        // A real junction the fake linker knows nothing about: Exists() is true,
        // TryGetTarget() is false — exactly the "cannot confirm" case. The fake
        // linker leaves a plain directory at the live path, so replace it.
        Directory.Delete(context.Live, recursive: false);
        context.Linker.Targets.Clear();

        var target = context.Temp.CreateDirectory("unknown-link-target");
        var realLinker = new WindowsJunctionLinker();
        Assert.True(realLinker.CreateJunction(context.Live, target, out var createError), createError);

        var result = await context.Service.DeleteGroupAsync(context.Installation, second.Id);

        Assert.False(result.Success);
        Assert.Contains("无法确认", result.Message);
        Assert.True(Directory.Exists(second.DirectoryPath));
        Assert.Empty(context.RecycleBin.SentPaths);
        Assert.True(realLinker.RemoveJunction(context.Live, out _));
        _ = first;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

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
