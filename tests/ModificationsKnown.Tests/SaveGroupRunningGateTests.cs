using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

/// <summary>
/// One fail-closed rule for every write: Running blocks, and Unknown blocks
/// exactly like Running. Initialization, instance activation and importing the
/// live saves must leave the link, the index and every group directory alone.
/// </summary>
public sealed class SaveGroupRunningGateTests
{
    [Theory]
    [InlineData(GameRunningState.Running, "正在运行")]
    [InlineData(GameRunningState.Unknown, "无法确认")]
    public async Task BlockedStates_LeaveLinkIndexAndGroupsUntouched(
        GameRunningState state,
        string expected)
    {
        using var context = new SaveGroupTestContext();
        var (first, _) = await PrepareAsync(context);
        var before = DescribeState(context);

        context.ProbeState = state;
        var init = await context.Service.InitializeAsync(context.Installation);
        var activation = await context.Service.EnsureActiveGroupAsync(context.Installation);
        var import = await context.Service.ImportLiveIntoNewGroupAsync(context.Installation);
        var apply = await context.Service.ApplyCurrentAsync(context.Installation, first.Id);

        Assert.Equal(SaveGroupInitializationState.Failed, init.State);
        Assert.Contains(expected, init.Message);
        Assert.Equal(SaveGroupActivationState.RunningLocked, activation.State);
        Assert.Contains(expected, activation.Message);
        Assert.False(import.Success);
        Assert.Contains(expected, import.Message);
        Assert.False(apply.Success);
        Assert.Contains(expected, apply.Message);

        Assert.Equal(before, DescribeState(context));
    }

    private static async Task<(SaveGroup First, SaveGroup Second)> PrepareAsync(SaveGroupTestContext context)
    {
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);
        var first = init.Groups.Single();

        var created = await context.Service.CreateEmptyAsync(context.Installation, "存档组 2");
        Assert.True(created.Success, created.Message);

        var applied = await context.Service.ApplyCurrentAsync(context.Installation, first.Id);
        Assert.True(applied.Success, applied.Message);

        // Give the live group a real save so an import would have work to do.
        context.WriteLiveFile("slot_1.sv", "live-save");
        return (first, Assert.IsType<SaveGroup>(created.Group));
    }

    private static string DescribeState(SaveGroupTestContext context)
    {
        var target = context.Linker.TryGetTarget(context.Live, out var resolved) ? resolved : "<none>";
        var catalog = File.Exists(context.CatalogPath) ? File.ReadAllText(context.CatalogPath) : "<none>";
        var files = Directory.Exists(context.ProfilesRoot)
            ? Directory.EnumerateFiles(context.ProfilesRoot, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => $"{Path.GetRelativePath(context.ProfilesRoot, path)}:{new FileInfo(path).Length}")
                .ToList()
            : [];

        return $"target={target}\ncatalog={catalog}\nfiles={string.Join(",", files)}";
    }
}
