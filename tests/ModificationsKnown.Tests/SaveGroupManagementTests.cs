using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

/// <summary>
/// Ticket 02: creating, cloning, renaming and selecting save groups. Every test
/// works inside a temporary directory; the player's real LocalLow is never used.
/// </summary>
public sealed class SaveGroupManagementTests
{
    [Fact]
    public async Task CreateEmpty_CreatesGroupWithoutSaves_AndSelectsFirstGroup()
    {
        using var context = new SaveGroupTestContext();

        var result = await context.Service.CreateEmptyAsync(context.Installation, "  新存档  ");

        Assert.True(result.Success, result.Message);
        var group = Assert.Single(context.Service.GetGroups(context.Installation));
        Assert.Equal("新存档", group.Name);
        Assert.Equal(group.Id, result.Group?.Id);
        Assert.Equal(group.Id, context.Service.GetNextLaunchGroupId(context.Installation));
        Assert.False(group.IsDefault);

        var dataFiles = Directory.EnumerateFiles(group.DirectoryPath, "*", SearchOption.AllDirectories)
            .Where(path => !SaveGroupFormat.IsInternalFile(Path.GetFileName(path)));
        Assert.Empty(dataFiles);
        Assert.True(SaveGroupManifestStore.TryRead(group.DirectoryPath, out var manifest));
        Assert.Equal(context.InstallationKey, manifest.InstallationKey);
        // 第一组创建后立即成为实际 Current。
        Assert.True(context.Linker.TryGetTarget(context.Live, out var liveTarget));
        Assert.Equal(group.DirectoryPath, liveTarget);
    }

    [Fact]
    public async Task CreateEmpty_SecondGroup_DoesNotChangeNextLaunchSelection()
    {
        using var context = new SaveGroupTestContext();
        var first = await context.Service.CreateEmptyAsync(context.Installation, "第一组");
        Assert.True(first.Success, first.Message);

        var second = await context.Service.CreateEmptyAsync(context.Installation, "第二组");

        Assert.True(second.Success, second.Message);
        Assert.Equal(2, context.Service.GetGroups(context.Installation).Count);
        Assert.Equal(first.Group!.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task CreateEmpty_RejectsNameAlreadyUsedIgnoringCase()
    {
        using var context = new SaveGroupTestContext();
        Assert.True((await context.Service.CreateEmptyAsync(context.Installation, "Main Run")).Success);

        var duplicate = await context.Service.CreateEmptyAsync(context.Installation, "main run");

        Assert.False(duplicate.Success);
        Assert.Contains("已存在", duplicate.Message);
        Assert.Single(context.Service.GetGroups(context.Installation));
    }

    [Fact]
    public async Task CreateEmpty_RejectsInvalidName()
    {
        using var context = new SaveGroupTestContext();

        var empty = await context.Service.CreateEmptyAsync(context.Installation, "   ");
        var invalid = await context.Service.CreateEmptyAsync(context.Installation, "a/b");

        Assert.False(empty.Success);
        Assert.Contains("不能为空", empty.Message);
        Assert.False(invalid.Success);
        Assert.Contains("特殊字符", invalid.Message);
        Assert.Empty(context.Service.GetGroups(context.Installation));
    }

    [Fact]
    public async Task CreateEmpty_WhenGameIsRunning_FailsWithoutWritingAnything()
    {
        using var context = new SaveGroupTestContext();
        context.Running = true;

        var result = await context.Service.CreateEmptyAsync(context.Installation, "组");

        Assert.False(result.Success);
        Assert.Contains("正在运行", result.Message);
        Assert.False(Directory.Exists(context.InstallationDirectory));
        Assert.Empty(context.Service.GetGroups(context.Installation));
    }

    [Fact]
    public async Task Clone_CopiesEveryFile_WithNewIdentity_AndLeavesSourceUntouched()
    {
        using var context = new SaveGroupTestContext();
        var source = await context.Service.CreateEmptyAsync(context.Installation, "源组");
        Assert.True(source.Success, source.Message);
        var sourceDirectory = source.Group!.DirectoryPath;
        WriteGroupFile(sourceDirectory, "save.sv", "save-data");
        WriteGroupFile(sourceDirectory, Path.Combine("slot1", "save.sv"), "slot-one");
        WriteGroupFile(sourceDirectory, Path.Combine("deep", "nested", "extra.bin"), "binary-ish");
        WriteGroupFile(sourceDirectory, SaveGroupFormat.LegacyProfileMarkerFileName, @"C:\Games\CU");

        var clone = await context.Service.CloneAsync(context.Installation, source.Group.Id, "克隆组");

        Assert.True(clone.Success, clone.Message);
        Assert.NotEqual(source.Group.Id, clone.Group!.Id);
        Assert.NotEqual(sourceDirectory, clone.Group.DirectoryPath);
        Assert.Equal("克隆组", clone.Group.Name);
        Assert.False(clone.Group.IsDefault);

        Assert.Equal("save-data", File.ReadAllText(Path.Combine(clone.Group.DirectoryPath, "save.sv")));
        Assert.Equal("slot-one",
            File.ReadAllText(Path.Combine(clone.Group.DirectoryPath, "slot1", "save.sv")));
        Assert.Equal("binary-ish",
            File.ReadAllText(Path.Combine(clone.Group.DirectoryPath, "deep", "nested", "extra.bin")));
        Assert.False(File.Exists(Path.Combine(clone.Group.DirectoryPath, SaveGroupFormat.LegacyProfileMarkerFileName)));

        Assert.True(SaveGroupManifestStore.TryRead(clone.Group.DirectoryPath, out var cloneManifest));
        Assert.Equal(clone.Group.Id, cloneManifest.GroupId);
        Assert.Equal("克隆组", cloneManifest.Name);

        // Source keeps its identity, manifest and files.
        Assert.True(SaveGroupManifestStore.TryRead(sourceDirectory, out var sourceManifest));
        Assert.Equal(source.Group.Id, sourceManifest.GroupId);
        Assert.Equal("源组", sourceManifest.Name);
        Assert.Equal("save-data", File.ReadAllText(Path.Combine(sourceDirectory, "save.sv")));

        Assert.Equal(2, context.Service.GetGroups(context.Installation).Count);
        Assert.Equal(source.Group.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task Clone_RejectsNameAlreadyUsed_AndRejectsUnknownSource()
    {
        using var context = new SaveGroupTestContext();
        var source = await context.Service.CreateEmptyAsync(context.Installation, "源组");
        Assert.True(source.Success, source.Message);

        var duplicate = await context.Service.CloneAsync(context.Installation, source.Group!.Id, "源组");
        var missing = await context.Service.CloneAsync(context.Installation, Guid.NewGuid(), "另一个");

        Assert.False(duplicate.Success);
        Assert.Contains("已存在", duplicate.Message);
        Assert.False(missing.Success);
        Assert.Contains("不存在", missing.Message);
        Assert.Single(context.Service.GetGroups(context.Installation));
    }

    [Fact]
    public async Task Clone_WhenGameIsRunning_FailsWithoutCopying()
    {
        using var context = new SaveGroupTestContext();
        var source = await context.Service.CreateEmptyAsync(context.Installation, "源组");
        Assert.True(source.Success, source.Message);
        WriteGroupFile(source.Group!.DirectoryPath, "save.sv", "save-data");

        context.Running = true;
        var clone = await context.Service.CloneAsync(context.Installation, source.Group.Id, "克隆组");

        Assert.False(clone.Success);
        Assert.Contains("正在运行", clone.Message);
        Assert.Single(context.Service.GetGroups(context.Installation));
    }

    [Fact]
    public async Task Rename_UpdatesManifestAndIndex_AndSurvivesReload()
    {
        using var context = new SaveGroupTestContext();
        var created = await context.Service.CreateEmptyAsync(context.Installation, "旧名字");
        Assert.True(created.Success, created.Message);

        var renamed = await context.Service.RenameAsync(context.Installation, created.Group!.Id, "新名字");

        Assert.True(renamed.Success, renamed.Message);
        Assert.Equal("新名字", renamed.Group!.Name);
        Assert.Equal(created.Group.DirectoryPath, renamed.Group.DirectoryPath);
        Assert.True(SaveGroupManifestStore.TryRead(created.Group.DirectoryPath, out var manifest));
        Assert.Equal("新名字", manifest.Name);
        Assert.Equal(created.Group.Id, manifest.GroupId);

        var reopened = context.BuildService();
        var group = Assert.Single(reopened.GetGroups(context.Installation));
        Assert.Equal("新名字", group.Name);
        Assert.Equal(created.Group.Id, group.Id);
    }

    [Fact]
    public async Task Rename_RejectsNameOfAnotherGroup_AndUnknownGroup()
    {
        using var context = new SaveGroupTestContext();
        var first = await context.Service.CreateEmptyAsync(context.Installation, "第一组");
        var second = await context.Service.CreateEmptyAsync(context.Installation, "第二组");
        Assert.True(first.Success && second.Success);

        var duplicate = await context.Service.RenameAsync(context.Installation, second.Group!.Id, "第一组");
        var missing = await context.Service.RenameAsync(context.Installation, Guid.NewGuid(), "随便");

        Assert.False(duplicate.Success);
        Assert.Contains("已存在", duplicate.Message);
        Assert.False(missing.Success);
        Assert.Contains("不存在", missing.Message);

        var groups = context.Service.GetGroups(context.Installation);
        Assert.Contains(groups, group => group.Name == "第一组");
        Assert.Contains(groups, group => group.Name == "第二组");
    }

    [Fact]
    public async Task Rename_WhenGameIsRunning_FailsAndKeepsOldName()
    {
        using var context = new SaveGroupTestContext();
        var created = await context.Service.CreateEmptyAsync(context.Installation, "旧名字");
        Assert.True(created.Success, created.Message);

        context.Running = true;
        var renamed = await context.Service.RenameAsync(context.Installation, created.Group!.Id, "新名字");

        Assert.False(renamed.Success);
        Assert.Contains("正在运行", renamed.Message);
        Assert.Equal("旧名字", Assert.Single(context.Service.GetGroups(context.Installation)).Name);
    }

    [Fact]
    public async Task SelectNextLaunch_PersistsAcrossServiceInstances_AndLeavesLiveLinkAlone()
    {
        using var context = new SaveGroupTestContext();
        var first = await context.Service.CreateEmptyAsync(context.Installation, "第一组");
        var second = await context.Service.CreateEmptyAsync(context.Installation, "第二组");
        Assert.True(first.Success && second.Success);

        var selected = await context.Service.SelectNextLaunchAsync(context.Installation, second.Group!.Id);

        Assert.True(selected.Success, selected.Message);
        Assert.Equal(second.Group.Id, selected.Group?.Id);
        Assert.Equal(second.Group.Id, context.BuildService().GetNextLaunchGroupId(context.Installation));
        // 只有偏好变了：链接仍指向第一组（首组创建即应用）。
        Assert.True(context.Linker.TryGetTarget(context.Live, out var current));
        Assert.Equal(first.Group!.DirectoryPath, current);

        var launch = await context.Service.PrepareLaunchAsync(context.Installation);
        Assert.True(launch.Success, launch.Message);
        Assert.True(context.Linker.TryGetTarget(context.Live, out var target));
        Assert.Equal(second.Group.DirectoryPath, target);
    }

    [Fact]
    public async Task SelectNextLaunch_WhenGameIsRunning_FailsAndKeepsSelection()
    {
        using var context = new SaveGroupTestContext();
        var first = await context.Service.CreateEmptyAsync(context.Installation, "第一组");
        var second = await context.Service.CreateEmptyAsync(context.Installation, "第二组");
        Assert.True(first.Success && second.Success);

        context.Running = true;
        var selected = await context.Service.SelectNextLaunchAsync(context.Installation, second.Group!.Id);

        Assert.False(selected.Success);
        Assert.Contains("正在运行", selected.Message);
        Assert.Equal(first.Group!.Id, context.Service.GetNextLaunchGroupId(context.Installation));
    }

    [Fact]
    public async Task SuggestGroupName_AvoidsDuplicatesAndSanitizesInvalidCharacters()
    {
        using var context = new SaveGroupTestContext();
        Assert.Equal("默认副本", context.Service.SuggestGroupName(context.Installation, "默认副本"));

        Assert.True((await context.Service.CreateEmptyAsync(context.Installation, "默认副本")).Success);
        var suggestion = context.Service.SuggestGroupName(context.Installation, "默认副本");
        Assert.NotEqual("默认副本", suggestion);
        Assert.True((await context.Service.CreateEmptyAsync(context.Installation, suggestion)).Success);

        var sanitized = context.Service.SuggestGroupName(context.Installation, "a/b:c");
        Assert.Null(SaveGroupFormat.ValidateName(sanitized));
        Assert.DoesNotContain('/', sanitized);
        Assert.DoesNotContain(':', sanitized);

        var empty = context.Service.SuggestGroupName(context.Installation, "   ");
        Assert.Null(SaveGroupFormat.ValidateName(empty));
    }

    [Fact]
    public async Task CreateEmpty_RejectsWhatTheSuggestionStrips_AndAcceptsTheSuggestion()
    {
        using var context = new SaveGroupTestContext();
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);

        var rejected = await context.Service.CreateEmptyAsync(context.Installation, "a/b:c");
        Assert.False(rejected.Success);
        Assert.Contains("特殊字符", rejected.Message);

        var suggestion = context.Service.SuggestGroupName(context.Installation, "a/b:c");
        var created = await context.Service.CreateEmptyAsync(context.Installation, suggestion);

        Assert.True(created.Success, created.Message);
        Assert.Equal(suggestion, created.Group?.Name);
    }

    [Fact]
    public async Task MultipleInstallations_KeepGroupsAndSelectionIndependent()
    {
        using var context = new SaveGroupTestContext();
        var secondDirectory = context.Temp.CreateDirectory("game-2");
        var second = new GameInstallation(KnownGameProfiles.Full, secondDirectory, null, false, false);

        var a1 = await context.Service.CreateEmptyAsync(context.Installation, "共享名");
        var b1 = await context.Service.CreateEmptyAsync(second, "共享名");
        var a2 = await context.Service.CreateEmptyAsync(context.Installation, "副组");

        Assert.True(a1.Success, a1.Message);
        Assert.True(b1.Success, b1.Message);
        Assert.True(a2.Success, a2.Message);

        Assert.Equal(2, context.Service.GetGroups(context.Installation).Count);
        var bGroups = context.Service.GetGroups(second);
        Assert.Single(bGroups);
        Assert.Equal(b1.Group!.Id, bGroups[0].Id);

        Assert.NotEqual(
            context.Service.GetInstallationDirectory(context.Installation),
            context.Service.GetInstallationDirectory(second));

        Assert.NotEqual(context.Service.GetInstallationKey(context.Installation),
            context.Service.GetInstallationKey(second));

        var selectForSecond = await context.Service.SelectNextLaunchAsync(second, b1.Group.Id);
        Assert.True(selectForSecond.Success, selectForSecond.Message);

        Assert.Equal(a1.Group!.Id, context.Service.GetNextLaunchGroupId(context.Installation));
        Assert.Equal(b1.Group.Id, context.Service.GetNextLaunchGroupId(second));

        // A group from one installation is never addressable from another.
        var crossInstallation = await context.Service.CloneAsync(second, a1.Group.Id, "跨目录克隆");
        Assert.False(crossInstallation.Success);
        Assert.Single(context.Service.GetGroups(second));
    }

    private static void WriteGroupFile(string groupDirectory, string relativePath, string content)
    {
        var path = Path.Combine(groupDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
