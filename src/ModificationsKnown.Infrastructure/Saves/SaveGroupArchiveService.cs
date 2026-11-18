using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>The game edition/version an archive came from. Metadata only: export
/// never reads or rewrites game data.</summary>
public sealed record SaveGameInfo(string Edition, string Version)
{
    public static SaveGameInfo Unknown { get; } = new(
        SaveGroupArchiveFormat.UnknownSource, SaveGroupArchiveFormat.UnknownSource);

    public static SaveGameInfo FromInstallation(GameInstallation installation, string? version = null) =>
        new(
            installation.Profile.Edition.ToString(),
            string.IsNullOrWhiteSpace(version) ? SaveGroupArchiveFormat.UnknownSource : version.Trim());
}

public sealed record SaveGroupExportResult(
    bool Success,
    string Message,
    string? ArchivePath,
    int FileCount,
    long TotalBytes);

public sealed record SaveGroupArchiveInspection(
    bool Success,
    string Message,
    SaveGroupArchiveManifest? Manifest,
    int EntryCount);

public enum SaveGroupImportState
{
    Imported,
    NeedsEditionConfirmation,
    Failed
}

public sealed record SaveGroupImportResult(
    SaveGroupImportState State,
    string Message,
    SaveGroup? Group = null,
    string? SuggestedName = null,
    string? SourceEdition = null,
    string? SourceVersion = null,
    string? TargetEdition = null,
    string? TargetVersion = null)
{
    public bool Success => State == SaveGroupImportState.Imported;
}

/// <summary>
/// Writes and reads the versioned save-group archive: <c>manifest.json</c> at
/// the archive root plus every group file under <c>files/</c>. Export writes a
/// temporary package next to the target, re-opens and verifies it (layout,
/// sizes, SHA-256), and only then moves it into place. Any failure or
/// cancellation removes the temporary package and leaves the source group
/// untouched. The target path is chosen by the caller and its parent directory
/// is never created implicitly.
/// </summary>
public sealed class SaveGroupArchiveService
{
    private const int BufferSize = 81920;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<bool>? _isGameRunning;

    public SaveGroupArchiveService(
        Func<DateTimeOffset>? utcNow = null,
        Func<bool>? isGameRunning = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _isGameRunning = isGameRunning;
    }

    public Task<SaveGroupExportResult> ExportAsync(
        SaveGroup group,
        SaveGameInfo source,
        string targetZipPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExportCore(group, source, targetZipPath, cancellationToken), cancellationToken);

    public Task<SaveGroupArchiveInspection> InspectAsync(
        string zipPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => InspectCore(zipPath, cancellationToken), cancellationToken);

    /// <summary>
    /// Imports an archive as a brand-new group. The archive is fully validated
    /// (schema, paths, duplicates, sizes, SHA-256) before anything is extracted;
    /// files go into a staging directory and are only registered after every
    /// hash matches. Any failure deletes staging and leaves existing groups and
    /// the source archive untouched. A cross-edition/version archive is refused
    /// until the caller confirms it.
    /// </summary>
    public Task<SaveGroupImportResult> ImportAsync(
        string zipPath,
        GameInstallation installation,
        SaveGroupService groups,
        string groupName,
        bool allowEditionMismatch = false,
        string? targetVersion = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => ImportCore(zipPath, installation, groups, groupName, allowEditionMismatch, targetVersion,
                cancellationToken),
            cancellationToken);

    private SaveGroupImportResult ImportCore(
        string zipPath,
        GameInstallation installation,
        SaveGroupService groups,
        string groupName,
        bool allowEditionMismatch,
        string? targetVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_isGameRunning?.Invoke() == true)
            return ImportFailure("游戏正在运行；请先关闭游戏再导入存档组。");

        if (groups is null) return ImportFailure("存档组服务不可用。");

        var inspection = InspectCore(zipPath, cancellationToken);
        if (!inspection.Success || inspection.Manifest is null)
            return ImportFailure(inspection.Message);

        var manifest = inspection.Manifest;
        var sourceEdition = NormalizeSource(manifest.SourceEdition);
        var sourceVersion = NormalizeSource(manifest.SourceVersion);
        var targetEdition = installation.Profile.Edition.ToString();
        var target = string.IsNullOrWhiteSpace(targetVersion)
            ? SaveGroupArchiveFormat.UnknownSource
            : targetVersion.Trim();

        var editionDiffers = !string.Equals(
            sourceEdition, SaveGroupArchiveFormat.UnknownSource, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sourceEdition, targetEdition, StringComparison.OrdinalIgnoreCase);
        var versionDiffers = !string.Equals(
            sourceVersion, SaveGroupArchiveFormat.UnknownSource, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target, SaveGroupArchiveFormat.UnknownSource, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sourceVersion, target, StringComparison.OrdinalIgnoreCase);

        if ((editionDiffers || versionDiffers) && !allowEditionMismatch)
        {
            return new SaveGroupImportResult(
                SaveGroupImportState.NeedsEditionConfirmation,
                $"归档来自{DescribeEdition(sourceEdition)}（{sourceVersion}），当前安装是" +
                $"{DescribeEdition(targetEdition)}（{target}）。导入只会新建存档组，不做格式转换，游戏可能无法读取。",
                SourceEdition: sourceEdition,
                SourceVersion: sourceVersion,
                TargetEdition: targetEdition,
                TargetVersion: target);
        }

        var invalidName = SaveGroupFormat.ValidateName(groupName);
        if (invalidName is not null) return ImportFailure(invalidName);

        var normalizedName = SaveGroupFormat.NormalizeName(groupName);
        var suggested = groups.SuggestGroupName(installation, normalizedName);
        if (!string.Equals(suggested, normalizedName, StringComparison.Ordinal))
        {
            return new SaveGroupImportResult(
                SaveGroupImportState.Failed,
                $"已存在同名存档组「{normalizedName}」；请改名后重试。",
                SuggestedName: suggested);
        }

        var conflict = FindPathConflict(manifest.Files);
        if (conflict is not null) return ImportFailure(conflict);

        string staging;
        try
        {
            Directory.CreateDirectory(groups.StagingRoot);
            staging = Path.Combine(groups.StagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
        }
        catch (Exception exception)
        {
            return ImportFailure($"无法创建导入暂存目录：{exception.Message}");
        }

        try
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var file in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entry = archive.GetEntry(SaveGroupArchiveFormat.FilesPrefix + file.Path);
                    if (entry is null)
                        throw new InvalidDataException($"归档缺少文件：{file.Path}");
                    if (entry.Length != file.Size)
                        throw new InvalidDataException($"归档文件大小不符：{file.Path}");

                    var destination = Path.Combine(
                        staging,
                        file.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!PathSafety.IsWithinDirectory(destination, staging))
                        throw new InvalidDataException($"归档路径越界：{file.Path}");

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = entry.Open();
                    using var output = new FileStream(
                        destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        long size = 0;
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            output.Write(buffer, 0, read);
                            size += read;
                        }

                        if (size != file.Size)
                            throw new InvalidDataException($"归档文件大小不符：{file.Path}");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    output.Flush();
                    var actual = ToLowerHex(hash.GetHashAndReset());
                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"归档文件校验失败：{file.Path}");
                }
            }

            var adopted = groups.AdoptGroup(installation, staging, normalizedName);
            if (!adopted.Success)
            {
                DeleteDirectoryQuietly(staging);
                return ImportFailure(adopted.Message);
            }

            return new SaveGroupImportResult(
                SaveGroupImportState.Imported,
                $"已导入存档组「{normalizedName}」，共 {manifest.Files.Count} 个文件并逐文件校验。",
                adopted.Group,
                SourceEdition: sourceEdition,
                SourceVersion: sourceVersion,
                TargetEdition: targetEdition,
                TargetVersion: target);
        }
        catch (OperationCanceledException)
        {
            DeleteDirectoryQuietly(staging);
            throw;
        }
        catch (Exception exception)
        {
            DeleteDirectoryQuietly(staging);
            return ImportFailure($"导入存档组失败，未登记新组：{exception.Message}");
        }
    }

    private static string? FindPathConflict(IReadOnlyList<SaveGroupArchiveFile> files)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!paths.Add(file.Path))
                return $"归档清单包含重复路径：{file.Path}";
        }

        foreach (var file in files)
        {
            var segments = file.Path.Split('/');
            var parent = string.Empty;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                parent = index == 0 ? segments[0] : $"{parent}/{segments[index]}";
                if (paths.Contains(parent))
                    return $"归档清单存在文件/目录路径冲突：{parent}";
            }
        }

        return null;
    }

    private static string DescribeEdition(string edition) => edition.ToLowerInvariant() switch
    {
        "full" => "正式版",
        "demo" => "试玩版",
        "unknown" => "未知版本",
        _ => edition
    };

    private static SaveGroupImportResult ImportFailure(string message) =>
        new(SaveGroupImportState.Failed, message);

    private static void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best effort; the caller reports the primary failure.
        }
    }

    private SaveGroupExportResult ExportCore(
        SaveGroup group,
        SaveGameInfo source,
        string targetZipPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_isGameRunning?.Invoke() == true)
            return Failure("游戏正在运行；请先关闭游戏再备份或导出存档组。");

        if (group is null) return Failure("没有可导出的存档组。");
        if (string.IsNullOrWhiteSpace(targetZipPath)) return Failure("请选择导出文件的目标路径。");

        string target;
        try
        {
            target = Path.GetFullPath(targetZipPath);
        }
        catch (Exception exception)
        {
            return Failure($"导出路径无效：{exception.Message}");
        }

        if (File.Exists(target)) return Failure($"目标文件已存在，未覆盖：{target}");
        if (Directory.Exists(target)) return Failure($"目标路径是一个目录：{target}");

        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            return Failure($"目标目录不存在：{parent ?? target}");

        if (!Directory.Exists(group.DirectoryPath))
        {
            return File.Exists(group.DirectoryPath)
                ? Failure($"存档组路径不是目录：{group.DirectoryPath}")
                : Failure($"存档组目录不存在：{group.DirectoryPath}");
        }

        var temporaryPath = $"{target}.tmp-{Guid.NewGuid():N}";
        try
        {
            var files = CollectFiles(group.DirectoryPath, cancellationToken);
            var manifest = WritePackage(temporaryPath, group, source, files, cancellationToken);
            VerifyPackage(temporaryPath, manifest, cancellationToken);
            File.Move(temporaryPath, target, overwrite: false);

            return new SaveGroupExportResult(
                true,
                $"已导出存档组「{group.Name}」（{manifest.FileCount} 个文件）。",
                target,
                manifest.FileCount,
                manifest.TotalBytes);
        }
        catch (OperationCanceledException)
        {
            DeleteFileQuietly(temporaryPath);
            throw;
        }
        catch (Exception exception)
        {
            DeleteFileQuietly(temporaryPath);
            return Failure($"导出存档组失败：{exception.Message}");
        }
    }

    private SaveGroupArchiveInspection InspectCore(string zipPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(zipPath)) return InspectionFailure("归档路径为空。");
        if (!File.Exists(zipPath)) return InspectionFailure($"归档文件不存在：{zipPath}");

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return Validate(archive, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return InspectionFailure($"无法读取归档：{exception.Message}");
        }
    }

    private SaveGroupArchiveManifest WritePackage(
        string temporaryPath,
        SaveGroup group,
        SaveGameInfo source,
        IReadOnlyList<ArchiveSourceFile> files,
        CancellationToken cancellationToken)
    {
        var manifest = new SaveGroupArchiveManifest
        {
            SchemaVersion = SaveGroupArchiveFormat.SchemaVersion,
            GroupId = group.Id,
            GroupName = group.Name,
            SourceEdition = NormalizeSource(source.Edition),
            SourceVersion = NormalizeSource(source.Version),
            ExportedAt = _utcNow(),
            FileCount = files.Count
        };

        using var stream = new FileStream(
            temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = archive.CreateEntry(
                    SaveGroupArchiveFormat.FilesPrefix + file.RelativePath, CompressionLevel.Optimal);
                using var destination = entry.Open();
                using var input = new FileStream(
                    file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                    FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                long size = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    destination.Write(buffer, 0, read);
                    size += read;
                }

                manifest.Files.Add(new SaveGroupArchiveFile(
                    file.RelativePath, size, ToLowerHex(hash.GetHashAndReset())));
                manifest.TotalBytes += size;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // The manifest is written last so every hash above is final.
        var manifestEntry = archive.CreateEntry(
            SaveGroupArchiveFormat.ManifestFileName, CompressionLevel.Optimal);
        using var manifestStream = manifestEntry.Open();
        JsonSerializer.Serialize(manifestStream, manifest, ManifestJsonOptions);

        return manifest;
    }

    private static void VerifyPackage(
        string temporaryPath,
        SaveGroupArchiveManifest written,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(temporaryPath);
        var inspection = Validate(archive, cancellationToken);
        if (!inspection.Success)
            throw new InvalidDataException($"临时归档校验失败：{inspection.Message}");

        var verified = inspection.Manifest!;
        if (verified.GroupId != written.GroupId
            || verified.FileCount != written.FileCount
            || verified.TotalBytes != written.TotalBytes
            || !verified.Files.SequenceEqual(written.Files))
        {
            throw new InvalidDataException("临时归档的清单与导出内容不一致。");
        }
    }

    private static SaveGroupArchiveInspection Validate(ZipArchive archive, CancellationToken cancellationToken)
    {
        var manifestEntry = archive.GetEntry(SaveGroupArchiveFormat.ManifestFileName);
        if (manifestEntry is null)
            return InspectionFailure($"归档缺少 {SaveGroupArchiveFormat.ManifestFileName}。");

        SaveGroupArchiveManifest? manifest;
        try
        {
            using var stream = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<SaveGroupArchiveManifest>(stream, ManifestJsonOptions);
        }
        catch (JsonException)
        {
            return InspectionFailure("归档清单不是有效的 JSON。");
        }

        if (manifest is null) return InspectionFailure("归档清单为空。");
        if (manifest.SchemaVersion != SaveGroupArchiveFormat.SchemaVersion)
            return InspectionFailure($"不支持的归档格式版本：{manifest.SchemaVersion}。");
        if (manifest.GroupId == Guid.Empty) return InspectionFailure("归档清单缺少存档组 ID。");
        if (string.IsNullOrWhiteSpace(manifest.GroupName)) return InspectionFailure("归档清单缺少存档组名称。");
        if (manifest.Files is null) return InspectionFailure("归档清单缺少文件列表。");
        if (manifest.FileCount != manifest.Files.Count)
            return InspectionFailure("归档清单的文件数量与文件列表不一致。");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file is null) return InspectionFailure("归档清单包含空文件条目。");
            if (!SaveGroupArchiveFormat.IsSafeRelativePath(file.Path))
                return InspectionFailure($"归档清单包含无效路径：{file.Path}");
            if (!seen.Add(file.Path))
                return InspectionFailure($"归档清单包含重复路径：{file.Path}");
            if (SaveGroupFormat.IsInternalFile(Path.GetFileName(file.Path)))
                return InspectionFailure($"归档清单包含 MK 内部文件：{file.Path}");
            if (file.Size < 0) return InspectionFailure($"归档清单包含无效文件大小：{file.Path}");
            if (!IsSha256(file.Sha256)) return InspectionFailure($"归档清单包含无效 SHA-256：{file.Path}");

            var entry = archive.GetEntry(SaveGroupArchiveFormat.FilesPrefix + file.Path);
            if (entry is null) return InspectionFailure($"归档缺少文件：{file.Path}");
            if (entry.Length != file.Size) return InspectionFailure($"归档文件大小不符：{file.Path}");
            if (!HashMatches(entry, file.Sha256)) return InspectionFailure($"归档文件校验失败：{file.Path}");

            totalBytes += file.Size;
        }

        if (totalBytes != manifest.TotalBytes)
            return InspectionFailure("归档清单的总大小与文件列表不一致。");

        var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!archiveNames.Add(entry.FullName))
                return InspectionFailure($"归档包含重复条目：{entry.FullName}");

            if (entry.FullName.EndsWith('/')) continue;
            if (string.Equals(entry.FullName, SaveGroupArchiveFormat.ManifestFileName, StringComparison.Ordinal))
                continue;
            if (!entry.FullName.StartsWith(SaveGroupArchiveFormat.FilesPrefix, StringComparison.Ordinal))
                return InspectionFailure($"归档包含未知条目：{entry.FullName}");

            var relative = entry.FullName[SaveGroupArchiveFormat.FilesPrefix.Length..];
            if (!seen.Contains(relative))
                return InspectionFailure($"归档包含清单未记录的文件：{relative}");
        }

        return new SaveGroupArchiveInspection(
            true, $"归档完整，共 {manifest.Files.Count} 个文件。", manifest, manifest.Files.Count);
    }

    private static IReadOnlyList<ArchiveSourceFile> CollectFiles(
        string groupDirectory,
        CancellationToken cancellationToken)
    {
        var files = new List<ArchiveSourceFile>();
        foreach (var path in Directory.EnumerateFiles(groupDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveGroupFormat.IsInternalFile(Path.GetFileName(path))) continue;

            files.Add(new ArchiveSourceFile(
                SaveGroupArchiveFormat.NormalizeRelativePath(
                    Path.GetRelativePath(groupDirectory, path).Replace('\\', '/')),
                path));
        }

        files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return files;
    }

    private static bool HashMatches(ZipArchiveEntry entry, string expected)
    {
        using var stream = entry.Open();
        return string.Equals(ToLowerHex(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string ToLowerHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    private static string NormalizeSource(string? value) =>
        string.IsNullOrWhiteSpace(value) ? SaveGroupArchiveFormat.UnknownSource : value.Trim();

    private static SaveGroupExportResult Failure(string message) =>
        new(false, message, null, 0, 0);

    private static SaveGroupArchiveInspection InspectionFailure(string message) =>
        new(false, message, null, 0);

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort: the caller already reports the primary failure.
        }
    }

    private sealed record ArchiveSourceFile(string RelativePath, string FullPath);
}
