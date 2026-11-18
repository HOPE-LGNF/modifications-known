using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

public sealed class SaveGroupArchiveTests
{
    private static readonly SaveGameInfo FullGame = new("Full", "1.2.3");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Export_WritesManifestAtRootAndFilesUnderPrefix()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        fixture.WriteSourceFile(Path.Combine("sub", "a.bin"), "binary");

        var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);

        Assert.True(result.Success, result.Message);
        Assert.Equal(fixture.Target, result.ArchivePath);
        Assert.Equal(2, result.FileCount);
        Assert.Equal(fixture.SourceFiles().Values.Sum(file => file.Size), result.TotalBytes);

        using var archive = ZipFile.OpenRead(fixture.Target);
        var names = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains(SaveGroupFormat.ArchiveManifestFileName, names);
        Assert.Contains("files/save.sv", names);
        Assert.Contains("files/sub/a.bin", names);
        Assert.All(names, name => Assert.DoesNotContain('\\', name));
        Assert.All(names, name => Assert.False(Path.IsPathRooted(name), name));
    }

    [Fact]
    public async Task Export_ManifestRecordsGroupMetadataAndMatchingHashes()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        fixture.WriteSourceFile(Path.Combine("sub", "a.bin"), "\u0001\u0002binary\u0000");

        var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);
        Assert.True(result.Success, result.Message);

        var manifest = fixture.ReadManifest();

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(fixture.Group.Id, manifest.GroupId);
        Assert.Equal(fixture.Group.Name, manifest.GroupName);
        Assert.Equal("Full", manifest.SourceEdition);
        Assert.Equal("1.2.3", manifest.SourceVersion);
        Assert.InRange(manifest.ExportedAt, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(2, manifest.FileCount);

        var expected = fixture.SourceFiles();
        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(
            expected.Keys.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            manifest.Files.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal).ToList());

        foreach (var file in manifest.Files)
        {
            Assert.Equal(expected[file.Path].Size, file.Size);
            Assert.Equal(expected[file.Path].Sha256, file.Sha256);
            Assert.Equal(file.Sha256.ToLowerInvariant(), file.Sha256);
        }

        Assert.Equal(manifest.Files.Sum(file => file.Size), manifest.TotalBytes);
        Assert.Equal(result.TotalBytes, manifest.TotalBytes);
    }

    [Fact]
    public async Task Export_ManifestContainsNoLocalOrAbsolutePaths()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");

        var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);
        Assert.True(result.Success, result.Message);

        var json = fixture.ReadManifestJson();

        // A JSON-escaped Windows path would show up as C:\\ and the temp root would
        // appear verbatim with doubled separators.
        Assert.DoesNotContain("C:\\\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Root.Replace("\\", "\\\\"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Group.DirectoryPath.Replace("\\", "\\\\"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Root.Replace("\\", "/"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installationKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("directoryPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_ExcludesInternalGroupFiles()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        fixture.WriteSourceFile(SaveGroupFormat.LegacyProfileMarkerFileName, @"C:\Games\CU");

        var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(["save.sv"], fixture.ReadManifest().Files.Select(file => file.Path).ToList());
    }

    [Fact]
    public async Task Export_EmptyGroup_ProducesManifestOnlyArchive()
    {
        using var fixture = new ArchiveFixture();

        var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.FileCount);
        Assert.Equal(0, result.TotalBytes);
        Assert.Empty(fixture.ReadManifest().Files);

        using var archive = ZipFile.OpenRead(fixture.Target);
        Assert.Equal([SaveGroupFormat.ArchiveManifestFileName], archive.Entries.Select(entry => entry.FullName).ToList());
    }

    [Fact]
    public async Task Export_UnknownVersion_IsRecordedAsUnknown()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");

        var result = await fixture.Service.ExportAsync(
            fixture.Group, new SaveGameInfo("Demo", "  "), fixture.Target);

        Assert.True(result.Success, result.Message);
        var manifest = fixture.ReadManifest();
        Assert.Equal("Demo", manifest.SourceEdition);
        Assert.Equal("unknown", manifest.SourceVersion);
    }

    [Fact]
    public async Task Export_DoesNotChangeSourceGroup()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        fixture.WriteSourceFile(Path.Combine("sub", "a.bin"), "binary");
        var before = fixture.SourceFiles();

        var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);

        Assert.True(result.Success, result.Message);
        fixture.AssertSourceUnchanged(before);
    }

    [Fact]
    public async Task Export_WhenTargetExists_DoesNotOverwrite()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        File.WriteAllText(fixture.Target, "sentinel");

        var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);

        Assert.False(result.Success);
        Assert.Contains("已存在", result.Message);
        Assert.Equal("sentinel", File.ReadAllText(fixture.Target));
        Assert.Empty(fixture.TemporaryFiles());
    }

    [Fact]
    public async Task Export_WhenParentDirectoryIsMissing_FailsWithoutCreatingIt()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        var target = Path.Combine(fixture.Root, "missing", "out.zip");

        var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, target);

        Assert.False(result.Success);
        Assert.False(File.Exists(target));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
        Assert.Empty(fixture.TemporaryFiles());
    }

    [Fact]
    public async Task Export_WhenSourceDirectoryIsMissing_Fails()
    {
        using var fixture = new ArchiveFixture();
        var group = fixture.Group with { DirectoryPath = Path.Combine(fixture.Root, "does-not-exist") };

        var result = await fixture.Service.ExportAsync(group, FullGame, fixture.Target);

        Assert.False(result.Success);
        Assert.False(File.Exists(fixture.Target));
        Assert.Empty(fixture.TemporaryFiles());
    }

    [Fact]
    public async Task Export_WhenSourceIsNotADirectory_Fails()
    {
        using var fixture = new ArchiveFixture();
        var filePath = Path.Combine(fixture.Root, "not-a-directory");
        File.WriteAllText(filePath, "x");
        var group = fixture.Group with { DirectoryPath = filePath };

        var result = await fixture.Service.ExportAsync(group, FullGame, fixture.Target);

        Assert.False(result.Success);
        Assert.False(File.Exists(fixture.Target));
    }

    [Fact]
    public async Task Export_WhenAFileCannotBeRead_FailsAndRemovesTemporaryArchive()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("a.sv", "first");
        var locked = fixture.WriteSourceFile("z.sv", "second");
        var before = fixture.SourceFiles();

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);

            Assert.False(result.Success);
            Assert.False(File.Exists(fixture.Target));
            Assert.Empty(fixture.TemporaryFiles());
        }

        fixture.AssertSourceUnchanged(before);
    }

    [Fact]
    public async Task Export_WhenCancelled_ThrowsAndWritesNothing()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target, cancellation.Token));

        Assert.False(File.Exists(fixture.Target));
        Assert.Empty(fixture.TemporaryFiles());
    }

    [Fact]
    public async Task Inspect_RoundTripsTheExportedManifest()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        fixture.WriteSourceFile(Path.Combine("sub", "a.bin"), "binary");

        var export = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);
        Assert.True(export.Success, export.Message);

        var inspection = await fixture.Service.InspectAsync(fixture.Target);

        Assert.True(inspection.Success, inspection.Message);
        Assert.Equal(2, inspection.EntryCount);
        var manifest = Assert.IsType<SaveGroupArchiveManifest>(inspection.Manifest);
        Assert.Equal(fixture.Group.Id, manifest.GroupId);
        Assert.Equal("Full", manifest.SourceEdition);
        Assert.Equal(export.FileCount, manifest.FileCount);
        Assert.Equal(export.TotalBytes, manifest.TotalBytes);

        var expected = fixture.SourceFiles();
        foreach (var file in manifest.Files)
        {
            Assert.Equal(expected[file.Path].Size, file.Size);
            Assert.Equal(expected[file.Path].Sha256, file.Sha256);
        }
    }

    [Fact]
    public async Task Inspect_WhenArchiveIsMissing_Fails()
    {
        using var fixture = new ArchiveFixture();

        var inspection = await fixture.Service.InspectAsync(Path.Combine(fixture.Root, "nope.zip"));

        Assert.False(inspection.Success);
        Assert.Null(inspection.Manifest);
        Assert.Equal(0, inspection.EntryCount);
    }

    [Fact]
    public async Task Inspect_WhenSchemaVersionIsUnsupported_Fails()
    {
        using var fixture = new ArchiveFixture();
        var manifest = fixture.NewManifest();
        manifest.SchemaVersion = 2;
        fixture.WriteArchive(manifest);

        var inspection = await fixture.Service.InspectAsync(fixture.Target);

        Assert.False(inspection.Success);
        Assert.Contains("版本", inspection.Message);
    }

    [Fact]
    public async Task Inspect_WhenAFileIsMissing_Fails()
    {
        using var fixture = new ArchiveFixture();
        var manifest = fixture.NewManifest();
        manifest.FileCount = 1;
        manifest.TotalBytes = 4;
        manifest.Files.Add(new SaveGroupArchiveFile("save.sv", 4, new string('a', 64)));
        fixture.WriteArchive(manifest);

        var inspection = await fixture.Service.InspectAsync(fixture.Target);

        Assert.False(inspection.Success);
        Assert.Contains("缺少", inspection.Message);
    }

    [Fact]
    public async Task Inspect_WhenFileHashDoesNotMatch_Fails()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        var export = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);
        Assert.True(export.Success, export.Message);

        var manifest = fixture.ReadManifest();
        var tampered = Path.Combine(fixture.Root, "tampered.zip");
        using (var source = ZipFile.OpenRead(fixture.Target))
        using (var destinationStream = File.Create(tampered))
        using (var destination = new ZipArchive(destinationStream, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                var copy = destination.CreateEntry(entry.FullName);
                using var output = copy.Open();
                if (entry.FullName == "files/save.sv")
                {
                    output.Write("tampered!"u8);
                }
                else
                {
                    using var input = entry.Open();
                    input.CopyTo(output);
                }
            }
        }

        var inspection = await fixture.Service.InspectAsync(tampered);

        Assert.False(inspection.Success);
        Assert.Contains("校验", inspection.Message);
        Assert.Equal(1, manifest.FileCount);
    }

    [Fact]
    public async Task Inspect_WhenManifestListsAPathOutsideFiles_Fails()
    {
        using var fixture = new ArchiveFixture();
        var manifest = fixture.NewManifest();
        manifest.FileCount = 1;
        manifest.TotalBytes = 4;
        manifest.Files.Add(new SaveGroupArchiveFile("../escape.sv", 4, new string('a', 64)));
        fixture.WriteArchive(manifest, ("files/../escape.sv", "data"u8.ToArray()));

        var inspection = await fixture.Service.InspectAsync(fixture.Target);

        Assert.False(inspection.Success);
        Assert.Contains("路径", inspection.Message);
    }

    [Fact]
    public async Task Export_WhenGameIsRunning_FailsWithoutWritingAnything()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        var service = new SaveGroupArchiveService(isGameRunning: () => true);

        var result = await service.ExportAsync(fixture.Group, FullGame, fixture.Target);

        Assert.False(result.Success);
        Assert.Contains("正在运行", result.Message);
        Assert.False(File.Exists(fixture.Target));
        Assert.Empty(fixture.TemporaryFiles());
    }

    [Fact]
    public async Task Inspect_RejectsDuplicateZipEntries()
    {
        using var fixture = new ArchiveFixture();
        var content = Encoding.UTF8.GetBytes("save-data");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var manifest = fixture.NewManifest();
        manifest.FileCount = 1;
        manifest.TotalBytes = content.Length;
        manifest.Files.Add(new SaveGroupArchiveFile("save.sv", content.Length, hash));
        fixture.WriteArchive(
            manifest,
            ("files/save.sv", content),
            ("files/save.sv", content));

        var inspection = await fixture.Service.InspectAsync(fixture.Target);

        Assert.False(inspection.Success);
        Assert.Contains("重复", inspection.Message);
    }

    [Fact]
    public async Task Export_RejectsATargetInsideTheSourceGroup()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        var before = fixture.SourceFiles();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.GroupDirectory, "backups")).FullName;
        var target = Path.Combine(nested, "backup.zip");

        var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, target);

        Assert.False(result.Success);
        Assert.Contains("存档组目录内", result.Message);
        Assert.False(File.Exists(target));
        fixture.AssertSourceUnchanged(before);
        Assert.Empty(fixture.TemporaryFiles());
    }

    [Fact]
    public async Task Export_TwiceToAnExternalPath_NeverEmbedsThePreviousArchive()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        var second = Path.Combine(fixture.TargetDirectory, "second.zip");

        var first = await fixture.Service.ExportAsync(fixture.Group, FullGame, fixture.Target);
        var again = await fixture.Service.ExportAsync(fixture.Group, FullGame, second);

        Assert.True(first.Success, first.Message);
        Assert.True(again.Success, again.Message);

        foreach (var path in new[] { fixture.Target, second })
        {
            using var archive = ZipFile.OpenRead(path);
            Assert.Single(archive.Entries, entry => entry.FullName == "files/save.sv");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Inspect_RejectsTooManyEntries()
    {
        using var fixture = new ArchiveFixture();
        var manifest = fixture.NewManifest();
        for (var index = 0; index <= SaveGroupArchiveLimits.MaxEntries; index++)
            manifest.Files.Add(new SaveGroupArchiveFile($"file_{index}.sv", 0, new string('0', 64)));
        manifest.FileCount = manifest.Files.Count;
        fixture.WriteArchive(manifest);

        var inspection = await fixture.Service.InspectAsync(fixture.Target);

        Assert.False(inspection.Success);
        Assert.Contains("超过上限", inspection.Message);
    }

    [Fact]
    public async Task Inspect_RejectsAnOversizedManifest()
    {
        using var fixture = new ArchiveFixture();
        var padding = Encoding.UTF8.GetBytes(
            "{\"SchemaVersion\":1,\"GroupId\":\"" + Guid.NewGuid() + "\",\"Files\":[],\"pad\":\"" +
            new string('x', SaveGroupArchiveLimits.MaxManifestBytes + 4096) + "\"}");

        using (var stream = File.Create(fixture.Target))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(SaveGroupFormat.ArchiveManifestFileName, CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write(padding);
        }

        var inspection = await fixture.Service.InspectAsync(fixture.Target);

        Assert.False(inspection.Success);
        Assert.Contains("归档清单过大", inspection.Message);
    }

    [Fact]
    public async Task Inspect_RejectsAnEntryDeclaredLargerThanTheSingleFileLimit()
    {
        using var fixture = new ArchiveFixture();
        var manifest = fixture.NewManifest();
        var size = SaveGroupArchiveLimits.MaxEntryBytes + 1;
        manifest.FileCount = 1;
        manifest.TotalBytes = size;
        manifest.Files.Add(new SaveGroupArchiveFile("huge.sv", size, new string('a', 64)));
        fixture.WriteArchive(manifest);

        var inspection = await fixture.Service.InspectAsync(fixture.Target);

        Assert.False(inspection.Success);
        Assert.Contains("单文件上限", inspection.Message);
    }

    [Fact]
    public async Task Inspect_RejectsACompressionBombBeforeHashingIt()
    {
        using var fixture = new ArchiveFixture();
        var payload = new byte[40 * 1024 * 1024];
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var manifest = fixture.NewManifest();
        manifest.FileCount = 1;
        manifest.TotalBytes = payload.Length;
        manifest.Files.Add(new SaveGroupArchiveFile("bomb.sv", payload.Length, hash));
        fixture.WriteArchive(manifest, ("files/bomb.sv", payload));

        var inspection = await fixture.Service.InspectAsync(fixture.Target);

        Assert.False(inspection.Success);
        Assert.Contains("压缩比", inspection.Message);
    }

    [Fact]
    public async Task Export_RejectsATargetReachedThroughTheLiveJunction()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var fixture = new ArchiveFixture();
        fixture.WriteSourceFile("save.sv", "save-data");
        fixture.CreateLiveJunction();
        var before = fixture.SourceFiles();

        // Textually this is not under GroupDirectory; the junction makes it so.
        var target = Path.Combine(fixture.LiveDirectory, "backup.zip");

        var result = await fixture.Service.ExportAsync(fixture.Group, FullGame, target);

        Assert.False(result.Success);
        Assert.Contains("存档组目录内", result.Message);
        Assert.False(File.Exists(Path.Combine(fixture.GroupDirectory, "backup.zip")));
        fixture.AssertSourceUnchanged(before);
    }

    [Fact]
    public async Task Export_HoldsTheSaveLock_SoAConcurrentDeleteWaits()
    {
        using var context = new SaveGroupTestContext();
        var created = await context.Service.CreateEmptyAsync(context.Installation, "导出组");
        Assert.True(created.Success, created.Message);
        var group = created.Group!;
        for (var index = 0; index < 5; index++)
            await File.WriteAllTextAsync(Path.Combine(group.DirectoryPath, $"save_{index}.sv"), $"data-{index}");

        using var fixture = new ArchiveFixture(context.Service);
        var target = Path.Combine(fixture.TargetDirectory, "export.zip");
        Task<SaveGroupOperationResult>? deletion = null;

        fixture.Service.FileWrittenHook = _ =>
        {
            deletion ??= Task.Run(
                () => context.Service.DeleteGroupAsync(context.Installation, group.Id));
            Thread.Sleep(250);

            // The delete must be waiting on the save lock held by the export.
            Assert.False(deletion.IsCompleted);
        };

        var result = await fixture.Service.ExportAsync(group, FullGame, target);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(deletion);
        var deleted = await deletion!;
        Assert.False(string.IsNullOrWhiteSpace(deleted.Message));

        using var archive = ZipFile.OpenRead(target);
        Assert.Equal(5, archive.Entries.Count(entry => entry.FullName.StartsWith("files/")));
    }

    [Fact]
    public async Task Export_WhenTheGameStartsMidway_DiscardsThePackage()
    {
        using var context = new SaveGroupTestContext();
        var created = await context.Service.CreateEmptyAsync(context.Installation, "导出组");
        Assert.True(created.Success, created.Message);
        var group = created.Group!;
        await File.WriteAllTextAsync(Path.Combine(group.DirectoryPath, "save.sv"), "data");

        var running = false;
        using var fixture = new ArchiveFixture(context.Service, () => running);
        var target = Path.Combine(fixture.TargetDirectory, "export.zip");
        fixture.Service.FileWrittenHook = _ => running = true;

        var result = await fixture.Service.ExportAsync(group, FullGame, target);

        Assert.False(result.Success);
        Assert.Contains("游戏", result.Message);
        Assert.False(File.Exists(target));
        Assert.Empty(fixture.TemporaryFiles());
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public ArchiveFixture(
            SaveGroupService? coordinator = null,
            Func<bool>? isGameRunning = null)
        {
            Service = new SaveGroupArchiveService(isGameRunning: isGameRunning, coordinator: coordinator);
            GroupDirectory = _temp.CreateDirectory("profiles", "install", "group");
            TargetDirectory = _temp.CreateDirectory("out");
            LiveDirectory = Path.Combine(_temp.Path, "LocalLow", "CasualtiesUnknown");
            Target = Path.Combine(TargetDirectory, "default.mk-save.zip");

            var id = Guid.NewGuid();
            Group = new SaveGroup(id, "默认", DateTimeOffset.UtcNow, true, GroupDirectory);
            SaveGroupManifestStore.Write(GroupDirectory, new SaveGroupManifest
            {
                GroupId = id,
                Name = Group.Name,
                CreatedAt = Group.CreatedAt,
                InstallationKey = "0123456789abcdef",
                IsDefault = true
            });
        }

        public string Root => _temp.Path;

        public string GroupDirectory { get; }

        public string LiveDirectory { get; }

        private readonly WindowsJunctionLinker _linker = new();

        public void CreateLiveJunction()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LiveDirectory)!);
            Assert.True(_linker.CreateJunction(LiveDirectory, GroupDirectory, out var error), error);
        }

        public string TargetDirectory { get; }

        public string Target { get; }

        public SaveGroup Group { get; }

        public SaveGroupArchiveService Service { get; }

        public string WriteSourceFile(string relative, string content)
        {
            var path = Path.Combine(GroupDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public Dictionary<string, (long Size, string Sha256)> SourceFiles()
        {
            var files = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(GroupDirectory, "*", SearchOption.AllDirectories))
            {
                if (SaveGroupFormat.IsInternalFile(Path.GetFileName(path))) continue;
                var relative = Path.GetRelativePath(GroupDirectory, path).Replace('\\', '/');
                files[relative] = (new FileInfo(path).Length, Sha256(path));
            }

            return files;
        }

        public void AssertSourceUnchanged(Dictionary<string, (long Size, string Sha256)> before)
        {
            var after = SourceFiles();
            Assert.Equal(before.Count, after.Count);
            foreach (var (path, expected) in before)
            {
                Assert.True(after.TryGetValue(path, out var actual), $"源文件丢失：{path}");
                Assert.Equal(expected.Size, actual.Size);
                Assert.Equal(expected.Sha256, actual.Sha256);
            }

            Assert.True(SaveGroupManifestStore.TryRead(GroupDirectory, out _));
        }

        public IReadOnlyList<string> TemporaryFiles() =>
            Directory.EnumerateFiles(TargetDirectory, "*.tmp-*", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(Root, "*.tmp-*", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public SaveGroupArchiveManifest ReadManifest() =>
            JsonSerializer.Deserialize<SaveGroupArchiveManifest>(ReadManifestJson(), JsonOptions)
            ?? throw new InvalidOperationException("manifest.json 无法解析。");

        public string ReadManifestJson()
        {
            using var archive = ZipFile.OpenRead(Target);
            var entry = archive.Entries.Single(candidate =>
                candidate.FullName == SaveGroupFormat.ArchiveManifestFileName);
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        public SaveGroupArchiveManifest NewManifest() => new()
        {
            GroupId = Group.Id,
            GroupName = Group.Name,
            SourceEdition = "Full",
            SourceVersion = "1.2.3",
            ExportedAt = DateTimeOffset.UtcNow,
            Files = []
        };

        public void WriteArchive(
            SaveGroupArchiveManifest manifest,
            params (string Name, byte[] Content)[] entries)
        {
            using var stream = File.Create(Target);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }

            var manifestEntry = archive.CreateEntry(SaveGroupFormat.ArchiveManifestFileName);
            using var manifestStream = manifestEntry.Open();
            JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        public void Dispose()
        {
            _linker.RemoveJunction(LiveDirectory, out _);
            _temp.Dispose();
        }

        private static string Sha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
}
