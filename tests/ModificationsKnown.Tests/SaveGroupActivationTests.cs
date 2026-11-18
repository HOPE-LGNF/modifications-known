using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

/// <summary>Alignment of the live junction when the active instance changes
/// (or the app starts). The service creates the first group, never touches
/// unmanaged saves on its own, and refuses while the game is running.</summary>
public sealed class SaveGroupActivationTests
{
    [Fact]
    public async Task EnsureActiveGroup_WithNoGroups_CreatesDefaultAndSwitches()
    {
        using var context = new SaveGroupTestContext();

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.Equal(SaveGroupActivationState.Switched, result.State);
        Assert.True(result.Success, result.Message);
        Assert.Equal("默认", result.Group?.Name);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(result.Group!.DirectoryPath, target);
        Assert.Equal(result.Group.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task EnsureActiveGroup_WhenAnotherGroupIsLive_SwitchesToRecordedGroup()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        Assert.True((await context.Service.ApplyCurrentAsync(context.Installation, first.Id)).Success);
        context.Linker.CreateJunction(context.Live, second.DirectoryPath, out _);

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.Equal(SaveGroupActivationState.Switched, result.State);
        Assert.True(result.Success, result.Message);
        Assert.Equal(first.Id, result.Group?.Id);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
    }

    [Fact]
    public async Task EnsureActiveGroup_WhenAlreadyLive_IsNoOp()
    {
        using var context = new SaveGroupTestContext();
        var (first, _) = await CreateTwoGroupsAsync(context);
        Assert.True((await context.Service.ApplyCurrentAsync(context.Installation, first.Id)).Success);

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.Equal(SaveGroupActivationState.AlreadyActive, result.State);
        Assert.True(result.Success, result.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
    }

    [Fact]
    public async Task EnsureActiveGroup_WhenGameIsRunning_IsLocked()
    {
        using var context = new SaveGroupTestContext();
        var (first, _) = await CreateTwoGroupsAsync(context);
        Assert.True((await context.Service.ApplyCurrentAsync(context.Installation, first.Id)).Success);
        context.Running = true;

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.Equal(SaveGroupActivationState.RunningLocked, result.State);
        Assert.False(result.Success);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
    }

    [Fact]
    public async Task EnsureActiveGroup_WhenProcessInfoIsUnreadable_IsLocked()
    {
        using var context = new SaveGroupTestContext();
        var (first, _) = await CreateTwoGroupsAsync(context);
        Assert.True((await context.Service.ApplyCurrentAsync(context.Installation, first.Id)).Success);
        context.ProbeState = GameRunningState.Unknown;

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.Equal(SaveGroupActivationState.RunningLocked, result.State);
        Assert.Contains("无法确认", result.Message);
    }

    [Fact]
    public async Task EnsureActiveGroup_WhenLinkSwitchFails_KeepsPreviousTarget()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        Assert.True((await context.Service.ApplyCurrentAsync(context.Installation, first.Id)).Success);
        Assert.True((await context.Service.SelectNextLaunchAsync(context.Installation, second.Id)).Success);
        context.Linker.FailNextCreate = 1;

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.Equal(SaveGroupActivationState.Failed, result.State);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
        Assert.Equal(second.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task EnsureActiveGroup_WithUnmanagedSaves_DoesNotTouchThem()
    {
        using var context = new SaveGroupTestContext();
        context.CreateLiveDirectory();
        context.WriteLiveFile("save.sv", "unmanaged");
        var before = Directory.GetFileSystemEntries(context.Live).Length;

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.Equal(SaveGroupActivationState.NeedsUnmanagedChoice, result.State);
        Assert.False(result.Success);
        Assert.Equal(before, Directory.GetFileSystemEntries(context.Live).Length);
        Assert.True(Directory.Exists(context.Live));

        var groupDirectories = Directory.Exists(context.InstallationDirectory)
            ? Directory.GetDirectories(context.InstallationDirectory)
            : [];
        Assert.Empty(groupDirectories);
    }

    [Fact]
    public async Task EnsureActiveGroup_WithUnmanagedSavesAndImportAction_MigratesAndSwitches()
    {
        using var context = new SaveGroupTestContext();
        context.CreateLiveDirectory();
        context.WriteLiveFile("save.sv", "unmanaged");

        var result = await context.Service.EnsureActiveGroupAsync(
            context.Installation,
            UnmanagedSaveAction.Import);

        Assert.Equal(SaveGroupActivationState.Switched, result.State);
        Assert.True(result.Success, result.Message);
        Assert.Equal("默认", result.Group?.Name);
        Assert.True(File.Exists(Path.Combine(result.Group!.DirectoryPath, "save.sv")));
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(result.Group.DirectoryPath, target);
    }

    [Fact]
    public async Task EnsureActiveGroup_WhenRecordedGroupDisappeared_FallsBackToDefault()
    {
        using var context = new SaveGroupTestContext();
        var (first, second) = await CreateTwoGroupsAsync(context);
        Assert.True((await context.Service.SelectNextLaunchAsync(context.Installation, second.Id)).Success);
        Directory.Delete(second.DirectoryPath, recursive: true);

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        Assert.Equal(first.Id, result.Group?.Id);
        Assert.Equal(first.Id, context.Service.GetNextLaunchGroupId(context.Installation));
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(first.DirectoryPath, target);
    }

    [Fact]
    public async Task EnsureActiveGroup_WhenLinkTargetIsGone_CreatesDefaultInsteadOfRefusing()
    {
        using var context = new SaveGroupTestContext();
        var missing = Path.Combine(context.ProfilesRoot, "0000000000000000", "00000000000000000000000000000000");
        context.Linker.CreateJunction(context.Live, missing, out _);

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        Assert.Equal("默认", result.Group?.Name);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(result.Group!.DirectoryPath, target);
    }

    [Fact]
    public async Task Initialize_WhenLinkTargetIsGone_CreatesDefaultInsteadOfRefusing()
    {
        using var context = new SaveGroupTestContext();
        var missing = Path.Combine(context.ProfilesRoot, "0000000000000000", "00000000000000000000000000000000");
        context.Linker.CreateJunction(context.Live, missing, out _);

        var init = await context.Service.InitializeAsync(context.Installation);

        Assert.True(init.Success, init.Message);
        Assert.Equal("默认", init.Groups.Single().Name);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(init.Groups.Single().DirectoryPath, target);
    }

    [Fact]
    public async Task EnsureActiveGroup_WhenLinkBelongsToAnotherInstallation_TakesItOver()
    {
        using var context = new SaveGroupTestContext();
        var otherGroup = Path.Combine(
            context.ProfilesRoot,
            "1111111111111111",
            "22222222222222222222222222222222");
        Directory.CreateDirectory(otherGroup);
        File.WriteAllText(Path.Combine(otherGroup, "save.sv"), "other installation");
        context.Linker.CreateJunction(context.Live, otherGroup, out _);

        var result = await context.Service.EnsureActiveGroupAsync(context.Installation);

        Assert.True(result.Success, result.Message);
        Assert.Equal("默认", result.Group?.Name);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(result.Group!.DirectoryPath, target);
        Assert.True(File.Exists(Path.Combine(otherGroup, "save.sv")));
    }

    [Fact]
    public async Task Initialize_WhenLinkBelongsToAnotherInstallation_DoesNotTouchIt()
    {
        using var context = new SaveGroupTestContext();
        var (_, activated) = await ActivateOtherInstallationAsync(context);

        var init = await context.Service.InitializeAsync(context.Installation);

        Assert.False(init.Success);
        Assert.Contains("另一个安装", init.Message);
        Assert.Contains("默认", init.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(activated.Group!.DirectoryPath, target);
        Assert.Empty(init.Groups);
    }

    [Fact]
    public async Task GetLiveOwner_ResolvesTheInstallationAndGroupThatOwnTheLink()
    {
        using var context = new SaveGroupTestContext();
        var (other, activated) = await ActivateOtherInstallationAsync(context);

        var owner = context.Service.GetLiveOwner();

        Assert.NotNull(owner);
        Assert.Equal(context.Service.GetInstallationKey(other), owner!.InstallationKey);
        Assert.Equal("默认", owner.Group.Name);
        Assert.Equal(activated.Group!.Id, owner.Group.Id);
    }

    [Fact]
    public void GetLiveOwner_WhenLinkIsDangling_ReturnsNull()
    {
        using var context = new SaveGroupTestContext();
        var missing = Path.Combine(context.ProfilesRoot, "0000000000000000", "00000000000000000000000000000000");
        context.Linker.CreateJunction(context.Live, missing, out _);

        Assert.Null(context.Service.GetLiveOwner());
    }

    private static async Task<(GameInstallation Other, SaveGroupActivationResult Activated)>
        ActivateOtherInstallationAsync(SaveGroupTestContext context)
    {
        var otherDir = context.Temp.CreateDirectory("other-game");
        var other = new GameInstallation(KnownGameProfiles.Demo, otherDir, null, false, false);
        var otherService = context.BuildService();
        var activated = await otherService.EnsureActiveGroupAsync(other);
        Assert.True(activated.Success, activated.Message);
        return (other, activated);
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
