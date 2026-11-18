using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

public sealed class SaveGroupImportTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task Import_CreatesNewGroupWithNewIdentity_AndKeepsSourceAndExistingGroups()
    {
        using var context = new SaveGroupTestContext();
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);
        var source = init.Groups.Single();
        File.WriteAllText(Path.Combine(source.DirectoryPath, "save.sv"), "imported-save");
        Directory.CreateDirectory(Path.Combine(source.DirectoryPath, "slot1"));
        File.WriteAllText(Path.Combine(source.DirectoryPath, "slot1", "save.sv"), "slot-one");

        var archiveService = new SaveGroupArchiveService();
        var archivePath = Path.Combine(context.Temp.Path, "export.zip");
        var export = await archiveService.ExportAsync(
            source,
            SaveGameInfo.FromInstallation(context.Installation),
            archivePath);
        Assert.True(export.Success, export.Message);

        var nextBefore = context.Service.GetNextLaunchGroupId(context.Installation);
        var import = await archiveService.ImportAsync(
            archivePath,
            context.Installation,
            context.Service,
            "导入的存档");

        Assert.True(import.Success, import.Message);
        var imported = Assert.IsType<SaveGroup>(import.Group);
        Assert.NotEqual(source.Id, imported.Id);
        Assert.Equal("导入的存档", imported.Name);
        Assert.False(imported.IsDefault);
        Assert.Equal("imported-save", File.ReadAllText(Path.Combine(imported.DirectoryPath, "save.sv")));
        Assert.Equal("slot-one", File.ReadAllText(Path.Combine(imported.DirectoryPath, "slot1", "save.sv")));

        Assert.Equal("imported-save", File.ReadAllText(Path.Combine(source.DirectoryPath, "save.sv")));
        Assert.Equal(nextBefore, context.Service.GetNextLaunchGroupId(context.Installation));
        Assert.True(File.Exists(archivePath));
        Assert.Equal(2, context.Service.GetGroups(context.Installation).Count);
        Assert.True(SaveGroupManifestStore.TryRead(imported.DirectoryPath, out var manifest));
        Assert.Equal(context.InstallationKey, manifest.InstallationKey);
        Assert.Empty(StagingDirectories(context));
    }

    [Fact]
    public async Task Import_CrossEdition_RequiresConfirmationAndCreatesNothingBeforeIt()
    {
        using var context = new SaveGroupTestContext();
        var source = await CreateExportedGroupAsync(context, "cross-edition.zip", "Full");
        var demoDirectory = context.Temp.CreateDirectory("demo-game");
        var demo = new GameInstallation(KnownGameProfiles.Demo, demoDirectory, null, false, false);
        await context.Service.InitializeAsync(demo);
        var before = context.Service.GetGroups(demo).Count;

        var archiveService = new SaveGroupArchiveService();
        var warning = await archiveService.ImportAsync(
            source.ArchivePath, demo, context.Service, "来自正式版");

        Assert.Equal(SaveGroupImportState.NeedsEditionConfirmation, warning.State);
        Assert.Contains("正式版", warning.Message);
        Assert.Equal(before, context.Service.GetGroups(demo).Count);
        Assert.Empty(StagingDirectories(context));

        var imported = await archiveService.ImportAsync(
            source.ArchivePath, demo, context.Service, "来自正式版", allowEditionMismatch: true);

        Assert.True(imported.Success, imported.Message);
        Assert.Equal(before + 1, context.Service.GetGroups(demo).Count);
        Assert.Equal("正式版存档", File.ReadAllText(
            Path.Combine(Assert.IsType<SaveGroup>(imported.Group).DirectoryPath, "save.sv")));
    }

    [Fact]
    public async Task Import_RejectsPathTraversalWithoutCreatingAnything()
    {
        using var context = new SaveGroupTestContext();
        var demo = new GameInstallation(
            KnownGameProfiles.Demo, context.Temp.CreateDirectory("demo-game"), null, false, false);
        var archivePath = Path.Combine(context.Temp.Path, "traversal.zip");
        WriteArchive(
            archivePath,
            Manifest(("Full", "../escape.sv", Encoding.UTF8.GetBytes("evil"))),
            ("files/../escape.sv", Encoding.UTF8.GetBytes("evil")));

        var result = await new SaveGroupArchiveService().ImportAsync(
            archivePath, demo, context.Service, "坏包");

        Assert.False(result.Success);
        Assert.Contains("路径", result.Message);
        Assert.Empty(context.Service.GetGroups(demo));
        Assert.Empty(StagingDirectories(context));
        Assert.False(File.Exists(Path.Combine(context.Temp.Path, "escape.sv")));
    }

    [Fact]
    public async Task Import_RejectsAbsolutePathWithoutCreatingAnything()
    {
        using var context = new SaveGroupTestContext();
        var demo = new GameInstallation(
            KnownGameProfiles.Demo, context.Temp.CreateDirectory("demo-game"), null, false, false);
        var archivePath = Path.Combine(context.Temp.Path, "absolute.zip");
        WriteArchive(
            archivePath,
            Manifest(("Full", "C:/Windows/evil.sv", Encoding.UTF8.GetBytes("evil"))),
            ("files/C:/Windows/evil.sv", Encoding.UTF8.GetBytes("evil")));

        var result = await new SaveGroupArchiveService().ImportAsync(
            archivePath, demo, context.Service, "坏包");

        Assert.False(result.Success);
        Assert.Empty(context.Service.GetGroups(demo));
        Assert.Empty(StagingDirectories(context));
    }

    [Fact]
    public async Task Import_RejectsHashMismatchWithoutCreatingAnything()
    {
        using var context = new SaveGroupTestContext();
        var demo = new GameInstallation(
            KnownGameProfiles.Demo, context.Temp.CreateDirectory("demo-game"), null, false, false);
        var archivePath = Path.Combine(context.Temp.Path, "bad-hash.zip");
        var manifest = Manifest(("Full", "save.sv", Encoding.UTF8.GetBytes("expected")));
        WriteArchive(archivePath, manifest, ("files/save.sv", Encoding.UTF8.GetBytes("tampered")));

        var result = await new SaveGroupArchiveService().ImportAsync(
            archivePath, demo, context.Service, "坏包");

        Assert.False(result.Success);
        Assert.Contains("校验", result.Message);
        Assert.Empty(context.Service.GetGroups(demo));
        Assert.Empty(StagingDirectories(context));
    }

    [Fact]
    public async Task Import_RejectsUnsupportedSchemaWithoutCreatingAnything()
    {
        using var context = new SaveGroupTestContext();
        var demo = new GameInstallation(
            KnownGameProfiles.Demo, context.Temp.CreateDirectory("demo-game"), null, false, false);
        var archivePath = Path.Combine(context.Temp.Path, "schema.zip");
        var manifest = Manifest(("Full", "save.sv", Encoding.UTF8.GetBytes("data")));
        manifest.SchemaVersion = 2;
        WriteArchive(archivePath, manifest, ("files/save.sv", Encoding.UTF8.GetBytes("data")));

        var result = await new SaveGroupArchiveService().ImportAsync(
            archivePath, demo, context.Service, "坏包");

        Assert.False(result.Success);
        Assert.Contains("版本", result.Message);
        Assert.Empty(context.Service.GetGroups(demo));
        Assert.Empty(StagingDirectories(context));
    }

    [Fact]
    public async Task Import_WhenNameExists_ReturnsSuggestionAndDoesNotOverwrite()
    {
        using var context = new SaveGroupTestContext();
        var source = await CreateExportedGroupAsync(context, "name-conflict.zip", "Full");
        var before = context.Service.GetGroups(context.Installation).Single();

        var result = await new SaveGroupArchiveService().ImportAsync(
            source.ArchivePath, context.Installation, context.Service, "默认");

        Assert.False(result.Success);
        Assert.Equal("默认 2", result.SuggestedName);
        Assert.Single(context.Service.GetGroups(context.Installation));
        Assert.Equal("默认", context.Service.GetGroups(context.Installation).Single().Name);
        Assert.Equal(before.Id, context.Service.GetGroups(context.Installation).Single().Id);
        Assert.Empty(StagingDirectories(context));
    }

    [Fact]
    public async Task Import_RejectsFileDirectoryPathConflictAndCleansStaging()
    {
        using var context = new SaveGroupTestContext();
        var demo = new GameInstallation(
            KnownGameProfiles.Full, context.Temp.CreateDirectory("full-game"), null, false, false);
        var archivePath = Path.Combine(context.Temp.Path, "conflict.zip");
        var fileContent = Encoding.UTF8.GetBytes("file");
        var nestedContent = Encoding.UTF8.GetBytes("nested");
        var manifest = Manifest(
            ("Full", "a", fileContent),
            ("Full", "a/b", nestedContent));
        WriteArchive(
            archivePath,
            manifest,
            ("files/a", fileContent),
            ("files/a/b", nestedContent));

        var result = await new SaveGroupArchiveService().ImportAsync(
            archivePath, demo, context.Service, "冲突包");

        Assert.False(result.Success);
        Assert.Contains("冲突", result.Message);
        Assert.Empty(context.Service.GetGroups(demo));
        Assert.Empty(StagingDirectories(context));
    }

    [Fact]
    public async Task Import_CrossVersion_RequiresConfirmationEvenForSameEdition()
    {
        using var context = new SaveGroupTestContext();
        var source = await CreateExportedGroupAsync(context, "cross-version.zip", "Full");

        var warning = await new SaveGroupArchiveService().ImportAsync(
            source.ArchivePath,
            context.Installation,
            context.Service,
            "旧版本存档",
            targetVersion: "2.0.0");

        Assert.Equal(SaveGroupImportState.NeedsEditionConfirmation, warning.State);
        Assert.Contains("1.0.0", warning.Message);
        Assert.Contains("2.0.0", warning.Message);
        Assert.Single(context.Service.GetGroups(context.Installation));
        Assert.Empty(StagingDirectories(context));
    }

    [Fact]
    public async Task Import_RejectsInternalManifestFiles()
    {
        using var context = new SaveGroupTestContext();
        var demo = new GameInstallation(
            KnownGameProfiles.Demo, context.Temp.CreateDirectory("demo-game"), null, false, false);
        var archivePath = Path.Combine(context.Temp.Path, "internal.zip");
        var content = Encoding.UTF8.GetBytes("marker");
        WriteArchive(
            archivePath,
            Manifest(("Full", SaveGroupFormat.ManifestFileName, content)),
            ($"files/{SaveGroupFormat.ManifestFileName}", content));

        var result = await new SaveGroupArchiveService().ImportAsync(
            archivePath, demo, context.Service, "内部文件");

        Assert.False(result.Success);
        Assert.Contains("内部文件", result.Message);
        Assert.Empty(context.Service.GetGroups(demo));
        Assert.Empty(StagingDirectories(context));
    }

    [Fact]
    public async Task Import_WhenGameIsRunning_FailsBeforeTouchingAnything()
    {
        using var context = new SaveGroupTestContext();
        var source = await CreateExportedGroupAsync(context, "running.zip", "Full");
        var running = new SaveGroupArchiveService(isGameRunning: () => true);

        var result = await running.ImportAsync(
            source.ArchivePath, context.Installation, context.Service, "运行中");

        Assert.False(result.Success);
        Assert.Contains("正在运行", result.Message);
        Assert.Single(context.Service.GetGroups(context.Installation));
        Assert.Empty(StagingDirectories(context));
    }

    [Fact]
    public async Task Import_WhenCancelled_LeavesNoGroupOrStaging()
    {
        using var context = new SaveGroupTestContext();
        var source = await CreateExportedGroupAsync(context, "cancel.zip", "Full");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SaveGroupArchiveService().ImportAsync(
                source.ArchivePath,
                context.Installation,
                context.Service,
                "取消导入",
                cancellationToken: cancellation.Token));

        Assert.Single(context.Service.GetGroups(context.Installation));
        Assert.Empty(StagingDirectories(context));
    }

    private static async Task<(string ArchivePath, SaveGroup Group)> CreateExportedGroupAsync(
        SaveGroupTestContext context,
        string archiveName,
        string edition)
    {
        var init = await context.Service.InitializeAsync(context.Installation);
        Assert.True(init.Success, init.Message);
        var group = init.Groups.Single();
        File.WriteAllText(Path.Combine(group.DirectoryPath, "save.sv"), "正式版存档");
        var archivePath = Path.Combine(context.Temp.Path, archiveName);
        var export = await new SaveGroupArchiveService().ExportAsync(
            group,
            new SaveGameInfo(edition, "1.0.0"),
            archivePath);
        Assert.True(export.Success, export.Message);
        return (archivePath, group);
    }

    private static IReadOnlyList<string> StagingDirectories(SaveGroupTestContext context) =>
        Directory.Exists(context.StagingRoot)
            ? Directory.EnumerateDirectories(context.StagingRoot).ToList()
            : [];

    private static SaveGroupArchiveManifest Manifest(params (string Edition, string Path, byte[] Content)[] files)
    {
        var manifest = new SaveGroupArchiveManifest
        {
            GroupId = Guid.NewGuid(),
            GroupName = "导入测试",
            SourceEdition = files.FirstOrDefault().Edition ?? "Full",
            SourceVersion = "1.0.0",
            ExportedAt = DateTimeOffset.UtcNow
        };

        foreach (var (_, path, content) in files)
        {
            manifest.Files.Add(new SaveGroupArchiveFile(
                path,
                content.Length,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()));
        }

        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(file => file.Size);
        return manifest;
    }

    private static void WriteArchive(
        string path,
        SaveGroupArchiveManifest manifest,
        params (string Name, byte[] Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var entryStream = entry.Open();
            entryStream.Write(content);
        }

        var manifestEntry = archive.CreateEntry(SaveGroupArchiveFormat.ManifestFileName);
        using var manifestStream = manifestEntry.Open();
        JsonSerializer.Serialize(manifestStream, manifest, JsonOptions);
    }
}
