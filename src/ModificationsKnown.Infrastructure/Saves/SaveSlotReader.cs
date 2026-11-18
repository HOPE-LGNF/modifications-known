using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// Reads whatever a save group directory contains and reports it as a list of
/// saves. The reader is deliberately forgiving: a missing group, a broken gzip
/// payload, a corrupt sidecar or an unknown file never throws; the problem is
/// described on the affected item instead.
/// </summary>
public sealed class SaveSlotReader
{
    private const string QolSlotsFileName = "qol_slots.json";

    private static readonly string[] SaveExtensions = [".sv", ".sav", ".save"];

    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "settings.json",
        QolSlotsFileName,
        "qol-mod-plugin-version-cache.json",
        "mp_settings.json"
    };

    private static readonly Regex SlotFilePattern = new(
        @"^slot_(\d+)\.sv$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DepthPrefixPattern = new(
        @"^\s*(\d+(?:\.\d+)?)\s*m\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AutosaveFilePattern = new(
        @"^auto[_-]?save([_-]\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public SaveGroupDetails ReadGroup(
        string groupDirectory,
        string? gameDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var language = SaveLanguage.Load(gameDirectory);

        if (string.IsNullOrWhiteSpace(groupDirectory) || !Directory.Exists(groupDirectory))
        {
            return new SaveGroupDetails
            {
                Layout = SaveLayoutKind.Empty,
                Error = "存档组目录不存在。"
            };
        }

        try
        {
            var root = Path.GetFullPath(groupDirectory);
            var candidates = EnumerateCandidates(root, cancellationToken);
            return Build(root, candidates, language, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SaveGroupDetails
            {
                Layout = SaveLayoutKind.Empty,
                Error = $"读取存档组失败：{exception.Message}"
            };
        }
    }

    private static SaveGroupDetails Build(
        string root,
        List<SaveCandidate> candidates,
        SaveLanguage language,
        CancellationToken cancellationToken)
    {
        var qolSlotsPath = Path.Combine(root, QolSlotsFileName);
        var hasQolSlotsFile = File.Exists(qolSlotsPath);
        var qol = hasQolSlotsFile ? ReadQolSlots(qolSlotsPath) : QolDeclaration.Empty;
        var declaredSlots = qol.Slots.Count;

        var slots = new SortedDictionary<int, SaveCandidate>();
        foreach (var candidate in candidates)
        {
            if (!candidate.IsRoot) continue;
            var match = SlotFilePattern.Match(candidate.FileName);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
            if (number <= 0) continue;
            slots[number] = candidate;
        }

        // An empty `Slots: []` must not by itself turn a vanilla save into a
        // multi-slot layout; at least one declared or actual slot is required.
        var isMultiSlot = slots.Count > 0 || declaredSlots > 0;

        var claims = new Dictionary<string, Claim>(StringComparer.OrdinalIgnoreCase);
        foreach (var (number, candidate) in slots)
            claims[candidate.Path] = new Claim(SaveArtifactKind.Slot, number);

        var hasVanillaSave = false;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (claims.ContainsKey(candidate.Path) || !candidate.IsRoot) continue;

            if (IsSaveFileName(candidate.FileName))
            {
                claims[candidate.Path] = new Claim(
                    isMultiSlot ? SaveArtifactKind.Current : SaveArtifactKind.SingleSave,
                    null);
                hasVanillaSave |= !isMultiSlot;
            }
            else if (IsAutosaveFileName(candidate.FileName))
            {
                claims[candidate.Path] = new Claim(SaveArtifactKind.Autosave, null);
            }
        }

        var entries = new List<SaveSlotMetadata>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Claim? claim = claims.TryGetValue(candidate.Path, out var claimed) ? claimed : null;
            var entry = CreateEntry(candidate, claim, qol.Names);
            entries.Add(entry);
        }

        var hasGeneric = entries.Exists(entry => entry.Kind == SaveArtifactKind.Generic);
        var layout = entries.Count == 0
            ? SaveLayoutKind.Empty
            : isMultiSlot
                ? SaveLayoutKind.MultiSlot
                : hasVanillaSave && !hasGeneric
                    ? SaveLayoutKind.SingleSave
                    : SaveLayoutKind.Generic;

        var saves = Deduplicate(entries);

        var ordered = saves
            .OrderBy(entry => KindRank(entry.Kind))
            .ThenBy(entry => entry.SlotNumber ?? int.MaxValue)
            .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var entry in ordered) FinalizeDerived(entry, language);

        var mostRecent = ordered
            .Where(entry => entry.SavedAt.HasValue)
            .OrderByDescending(entry => entry.SavedAt)
            .ThenBy(entry => entry.SlotNumber ?? int.MaxValue)
            .ThenBy(entry => entry.FileName, StringComparer.Ordinal)
            .FirstOrDefault();

        if (mostRecent is not null) mostRecent.IsMostRecent = true;

        var occupiedSlots = ordered
            .Where(entry => entry.Kind == SaveArtifactKind.Slot && entry.SlotNumber.HasValue)
            .Select(entry => entry.SlotNumber!.Value)
            .Distinct()
            .Count();

        var slotCount = layout switch
        {
            SaveLayoutKind.MultiSlot => Math.Max(declaredSlots, slots.Count == 0 ? 0 : slots.Keys.Max()),
            SaveLayoutKind.SingleSave => 1,
            _ => 0
        };

        var occupied = slots.Keys.ToHashSet();
        var emptySlots = qol.Slots
            .Where(number => !occupied.Contains(number))
            .Distinct()
            .OrderBy(number => number)
            .ToList();

        return new SaveGroupDetails
        {
            Layout = layout,
            Saves = ordered,
            SlotCount = slotCount,
            DeclaredSlotCount = declaredSlots,
            EmptySlotCount = declaredSlots > 0 ? Math.Max(0, declaredSlots - occupiedSlots) : 0,
            EmptySlots = emptySlots,
            MostRecentLabel = mostRecent?.Label,
            MostRecentDetail = mostRecent is null ? null : DescribeRecent(mostRecent),
            SummaryLabel = BuildSummary(layout, ordered, slotCount, mostRecent)
        };
    }

    private static SaveSlotMetadata CreateEntry(
        SaveCandidate candidate,
        Claim? claim,
        IReadOnlyDictionary<int, string> slotNames)
    {
        var customName = claim?.SlotNumber is { } slot
            ? slotNames.GetValueOrDefault(slot)
            : null;

        var entry = new SaveSlotMetadata
        {
            FilePath = candidate.Path,
            FileName = candidate.FileName,
            Kind = claim?.Kind ?? SaveArtifactKind.Generic,
            SlotNumber = claim?.SlotNumber,
            CustomName = customName,
            Label = BuildLabel(candidate, claim, customName)
        };

        try
        {
            entry.FileSize = new FileInfo(candidate.Path).Length;
        }
        catch (Exception)
        {
            // The hash step below reports the real read failure.
        }

        // The game writes "<save file>.meta" (slot_5.sv.meta, save.sv.meta), not
        // "slot_5.meta". The old replacement form is still read as a fallback so
        // groups written by earlier builds keep their metadata.
        var metaPath = candidate.Path + ".meta";
        if (!File.Exists(metaPath))
            metaPath = FindLegacyMeta(candidate.Path);

        if (metaPath is not null)
        {
            entry.MetaPath = metaPath;
            ReadMetadata(entry, metaPath);
        }

        ReadContent(entry);
        entry.SavedAt ??= SafeLastWriteTime(candidate.Path);
        return entry;
    }

    private static string BuildLabel(SaveCandidate candidate, Claim? claim, string? customName) => claim?.Kind switch
    {
        SaveArtifactKind.Slot when !string.IsNullOrWhiteSpace(customName) => $"槽 {claim.Value.SlotNumber} · {customName.Trim()}",
        SaveArtifactKind.Slot => $"槽 {claim.Value.SlotNumber}",
        SaveArtifactKind.SingleSave => "存档 1",
        SaveArtifactKind.Current => "当前进度",
        SaveArtifactKind.Autosave => "自动存档",
        _ => candidate.FileName
    };

    /// <summary>
    /// Older builds wrote <c>slot_1.meta</c> instead of <c>slot_1.sv.meta</c>.
    /// That form is ambiguous when the same stem exists with another save
    /// extension (<c>slot_1.sav</c>), so it is only used when the stem is unique.
    /// </summary>
    private static string? FindLegacyMeta(string savePath)
    {
        var legacy = Path.ChangeExtension(savePath, ".meta");
        if (!File.Exists(legacy)) return null;

        var directory = Path.GetDirectoryName(savePath);
        if (directory is null) return null;

        var stem = Path.GetFileNameWithoutExtension(savePath);
        var ownExtension = Path.GetExtension(savePath);
        foreach (var extension in SaveExtensions)
        {
            if (string.Equals(extension, ownExtension, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(directory, stem + extension))) return null;
        }

        return legacy;
    }

    private static void ReadMetadata(SaveSlotMetadata entry, string metaPath)
    {
        JsonDocument document;
        try
        {
            var info = new FileInfo(metaPath);
            if (info.Length > SaveGroupArchiveLimits.MaxManifestBytes)
            {
                entry.Error ??= "存档元数据文件过大，未读取。";
                return;
            }

            document = JsonDocument.Parse(
                File.ReadAllText(metaPath, Encoding.UTF8),
                new JsonDocumentOptions { MaxDepth = SaveGroupArchiveLimits.MaxManifestDepth });
        }
        catch (Exception exception)
        {
            entry.Error ??= $"存档元数据无法解析：{exception.Message}";
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                entry.Error ??= "存档元数据无法解析。";
                return;
            }

            entry.HasMetadata = true;
            entry.LocationName = ReadString(FindProperty(root, "LocationName", "Location", "Area")) ?? entry.LocationName;
            entry.DepthMeters ??= ParseDepth(entry.LocationName);
            entry.SavedAt = ReadTimestamp(FindProperty(root, "Timestamp", "SavedAt", "SavedAtUtc")) ?? entry.SavedAt;
            entry.LayerIndex = ReadInt(FindProperty(root, "BiomeIndex", "Biome")) ?? entry.LayerIndex;
            entry.IgtSeconds = ReadDouble(FindProperty(root, "IGTSeconds", "IgtSeconds", "RunTime")) ?? entry.IgtSeconds;
            entry.SaveKind = ReadString(FindProperty(root, "SaveKind")) ?? entry.SaveKind;
            entry.SeedValue = ReadLong(FindProperty(root, "SeedValue", "Seed")) ?? entry.SeedValue;
            entry.DepthMeters = ReadDouble(FindProperty(root, "DepthMeters", "Depth")) ?? entry.DepthMeters;
            entry.Cheated = ReadBool(FindProperty(root, "Cheated", "IsCheated")) ?? entry.Cheated;
            entry.IsMultiplayer = ReadBool(FindProperty(root, "IsMultiplayer", "Multiplayer")) ?? entry.IsMultiplayer;
            entry.IsMercRun = ReadBool(FindProperty(root, "IsMercRun")) ?? entry.IsMercRun;

            if (FindProperty(root, "Mods") is { ValueKind: JsonValueKind.Array } mods)
            {
                entry.ModCount = mods.GetArrayLength();
                entry.IsModded = entry.ModCount > 0;
            }
            else if (ReadInt(FindProperty(root, "ModCount")) is { } modCount)
            {
                entry.ModCount = Math.Max(0, modCount);
                entry.IsModded = entry.ModCount > 0;
            }
        }
    }

    private static void ReadContent(SaveSlotMetadata entry)
    {
        long fileSize;
        try
        {
            fileSize = new FileInfo(entry.FilePath).Length;
        }
        catch (Exception exception)
        {
            entry.IsReadable = false;
            entry.Error ??= $"无法读取存档文件：{exception.Message}";
            return;
        }

        if (fileSize > SaveGroupArchiveLimits.MaxSaveFileBytes)
        {
            entry.FileSize = fileSize;
            entry.IsReadable = false;
            entry.Error ??= "存档文件超过大小上限，未读取。";
            return;
        }

        string hash;
        try
        {
            hash = HashFile(entry.FilePath, out fileSize);
        }
        catch (Exception exception)
        {
            entry.IsReadable = false;
            entry.Error ??= $"无法读取存档文件：{exception.Message}";
            return;
        }

        entry.FileSize = fileSize;
        entry.ContentHash = hash;

        if (fileSize == 0)
        {
            // A 0-byte <c>.sv</c> is a placeholder the game (or a mod) created,
            // not a corrupt save: report it as an empty file, keep it listed and
            // do not mark it unreadable.
            entry.IsEmptyFile = true;
            return;
        }

        JsonDocument? document = null;
        try
        {
            using var stream = new FileStream(
                entry.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.SequentialScan);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var limited = new CappedReadStream(
                gzip, SaveGroupArchiveLimits.MaxSaveJsonBytes, "存档解压数据");
            document = JsonDocument.Parse(
                limited,
                new JsonDocumentOptions { MaxDepth = SaveGroupArchiveLimits.MaxSaveJsonDepth });
        }
        catch (StreamLimitExceededException)
        {
            document?.Dispose();
            entry.IsReadable = false;
            entry.Error ??= "存档数据超过解压上限，无法读取。";
            return;
        }
        catch (Exception)
        {
            document?.Dispose();
            entry.IsReadable = false;
            entry.Error ??= "无法识别的存档格式。";
            return;
        }

        using (document)
        {
            try
            {
                ApplySaveJson(entry, document.RootElement);
            }
            catch (Exception)
            {
                entry.IsReadable = false;
                entry.Error ??= "无法解析存档内容。";
            }
        }
    }

    private static void ApplySaveJson(SaveSlotMetadata entry, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            entry.IsReadable = false;
            entry.Error ??= "存档内容不是 JSON 对象。";
            return;
        }

        var payload = FindPayload(root);

        if (ReadInt(ReadProperty(payload, root, "biome", "biomeIndex", "BiomeIndex")) is { } layer && entry.LayerIndex is null)
            entry.LayerIndex = layer;

        if (ReadDouble(ReadProperty(payload, root, "runTime", "runtime", "runTimeSeconds", "igt", "igtSeconds", "playTime")) is { } seconds
            && entry.IgtSeconds is null)
            entry.IgtSeconds = seconds;

        if (entry.DifficultyKey is null)
        {
            var settings = ReadRunSettings(payload, root);
            if (settings.Count > 0)
            {
                var preset = SaveDifficultyPresets.Match(settings);
                entry.DifficultyKey = preset?.Key ?? SaveDifficultyPresets.CustomKey;
            }
        }

        if (ReadInt(ReadProperty(payload, root, "cId", "characterId", "CharacterId")) is { } characterId && entry.CharacterId is null)
            entry.CharacterId = characterId;

        // 0 = Experiment, 1 = Milky, 2 = Dune; keep the raw value so an unknown
        // index can still be shown instead of silently dropped.
        if (ReadDouble(ReadProperty(payload, root, "character", "Character")) is { } character && entry.CharacterValue is null)
            entry.CharacterValue = character;

        if (ReadBool(ReadProperty(payload, root, "isMercRun", "IsMercRun")) is { } mercRun)
            entry.IsMercRun |= mercRun;

        if (ReadBool(ReadProperty(payload, root, "isMultiplayer", "multiplayer", "Multiplayer")) is { } multiplayer)
            entry.IsMultiplayer |= multiplayer;

        if (ReadBool(ReadProperty(payload, root, "cheated", "isCheated")) is { } cheated)
            entry.Cheated |= cheated;

        if (ReadDouble(ReadProperty(payload, root, "depth", "maxDepth", "depthMeters")) is { } depth && entry.DepthMeters is null)
            entry.DepthMeters = depth;

        if (ReadLong(ReadProperty(payload, root, "seed", "seedValue", "SeedValue")) is { } seed && entry.SeedValue is null)
            entry.SeedValue = seed;
    }

    private static JsonElement FindPayload(JsonElement root)
    {
        foreach (var name in new[] { "runData", "run", "saveData", "gameSave", "data" })
        {
            if (FindProperty(root, name) is { ValueKind: JsonValueKind.Object } nested) return nested;
        }

        return root;
    }

    private static Dictionary<string, SaveSettingValue> ReadRunSettings(JsonElement payload, JsonElement root)
    {
        var result = new Dictionary<string, SaveSettingValue>(StringComparer.Ordinal);
        if (ReadProperty(payload, root, "runSettings", "run_settings") is not { } settings) return result;

        // Some builds serialise the list as a plain key/value object.
        if (settings.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in settings.EnumerateObject())
                result[property.Name] = ReadSettingValue(property.Value);

            return result;
        }

        if (settings.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in settings.EnumerateArray())
        {
            string? key = null;
            JsonElement? raw = null;

            if (item.ValueKind == JsonValueKind.Object)
            {
                key = ReadString(FindProperty(item, "Item1", "key", "Key", "name", "Name"));
                raw = FindProperty(item, "Item2", "value", "Value");
            }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                var parts = item.EnumerateArray().ToList();
                if (parts.Count >= 2)
                {
                    key = ReadString(parts[0]);
                    raw = parts[1];
                }
            }

            if (string.IsNullOrEmpty(key)) continue;
            result[key] = ReadSettingValue(raw);
        }

        return result;
    }

    /// <summary>
    /// Preserves the JSON type: a string "1" stays text, not a number. A value
    /// that cannot be interpreted (object, array, null, a missing Item2) becomes
    /// <see cref="SaveSettingValue.Unsupported"/> instead of being dropped —
    /// dropping it made a save with 51 known keys plus one unknown key look like
    /// an exact official preset.
    /// </summary>
    private static SaveSettingValue ReadSettingValue(JsonElement? element)
    {
        if (element is not { } value) return SaveSettingValue.Unsupported;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => SaveSettingValue.FromNumber(number),
            JsonValueKind.True => SaveSettingValue.FromBoolean(true),
            JsonValueKind.False => SaveSettingValue.FromBoolean(false),
            JsonValueKind.String => SaveSettingValue.FromText(value.GetString() ?? string.Empty),
            _ => SaveSettingValue.Unsupported
        };
    }

    private static List<SaveSlotMetadata> Deduplicate(List<SaveSlotMetadata> entries)
    {
        var groups = new List<List<SaveSlotMetadata>>(entries.Count);
        var byHash = new Dictionary<string, List<SaveSlotMetadata>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.ContentHash) || entry.FileSize == 0)
            {
                // A failed hash (or two empty files) must never merge unrelated saves.
                groups.Add([entry]);
                continue;
            }

            if (!byHash.TryGetValue(entry.ContentHash, out var group))
            {
                group = [];
                byHash[entry.ContentHash] = group;
                groups.Add(group);
            }

            group.Add(entry);
        }

        return groups.Select(MergeGroup).ToList();
    }

    private static SaveSlotMetadata MergeGroup(List<SaveSlotMetadata> group)
    {
        if (group.Count == 1) return group[0];

        var ordered = group
            .OrderBy(entry => KindRank(entry.Kind))
            .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var primary = ordered[0];
        var labels = ordered
            .Select(MergeLabel)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        primary.Label = string.Join(" · ", labels);

        foreach (var other in ordered.Skip(1))
        {
            primary.MetaPath ??= other.MetaPath;
            primary.LocationName ??= other.LocationName;
            primary.DepthMeters ??= other.DepthMeters;
            primary.LayerIndex ??= other.LayerIndex;
            primary.LayerName ??= other.LayerName;
            primary.DifficultyKey ??= other.DifficultyKey;
            primary.IgtSeconds ??= other.IgtSeconds;
            primary.SaveKind ??= other.SaveKind;
            primary.SeedValue ??= other.SeedValue;
            primary.CharacterId ??= other.CharacterId;
            primary.IsMultiplayer |= other.IsMultiplayer;
            primary.IsMercRun |= other.IsMercRun;
            primary.Cheated |= other.Cheated;
            primary.CustomName ??= other.CustomName;
            primary.ModCount = Math.Max(primary.ModCount, other.ModCount);
            primary.IsModded |= other.IsModded;
            primary.HasMetadata |= other.HasMetadata;
            primary.IsReadable |= other.IsReadable;
            primary.IsEmptyFile |= other.IsEmptyFile;
            if (string.IsNullOrEmpty(primary.ContentHash)) primary.ContentHash = other.ContentHash;
            if (string.IsNullOrWhiteSpace(primary.Error)) primary.Error = other.Error;
            if (other.SavedAt is { } savedAt && (primary.SavedAt is null || savedAt > primary.SavedAt))
                primary.SavedAt = savedAt;
        }

        return primary;
    }

    /// <summary>Compact form used when several files carry identical content.</summary>
    private static string MergeLabel(SaveSlotMetadata entry) => entry.Kind switch
    {
        SaveArtifactKind.Slot when entry.SlotNumber.HasValue && !string.IsNullOrWhiteSpace(entry.CustomName) =>
            $"槽 {entry.SlotNumber} · {entry.CustomName.Trim()}",
        SaveArtifactKind.Slot when entry.SlotNumber.HasValue => $"槽 {entry.SlotNumber}",
        SaveArtifactKind.Current => "当前",
        SaveArtifactKind.SingleSave => "存档 1",
        SaveArtifactKind.Autosave => "自动存档",
        _ => entry.Label
    };

    private static void FinalizeDerived(SaveSlotMetadata entry, SaveLanguage language)
    {
        if (entry.LayerIndex is { } layerIndex)
            entry.LayerName ??= language.LayerNameOrFallback(layerIndex);

        if (entry.IgtSeconds is { } seconds)
            entry.RunTimeLabel = FormatDuration(seconds);

        if (!string.IsNullOrEmpty(entry.DifficultyKey))
            entry.DifficultyLabel = language.PresetLabel(entry.DifficultyKey);

        if (entry.CharacterValue is { } character)
            entry.CharacterLabel = language.CharacterLabel(character);

        if (entry.SavedAt is { } savedAt)
            entry.SavedAtLabel = savedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string BuildSummary(
        SaveLayoutKind layout,
        IReadOnlyList<SaveSlotMetadata> saves,
        int slotCount,
        SaveSlotMetadata? mostRecent)
    {
        if (layout == SaveLayoutKind.Empty) return "尚无存档文件";
        if (layout == SaveLayoutKind.Generic) return $"{saves.Count} 个存档（未识别布局）";

        var autosaves = saves.Count(save => save.Kind == SaveArtifactKind.Autosave);
        var baseLabel = layout == SaveLayoutKind.SingleSave
            ? "1 个存档（原版单槽）"
            : slotCount > 0 ? $"{slotCount} 个槽位" : "尚无存档文件";
        if (autosaves > 0) baseLabel += $" · {autosaves} 个自动存档";

        return mostRecent is null ? baseLabel : $"{baseLabel} · 最近保存：{DescribeRecent(mostRecent)}";
    }

    /// <summary>e.g. "槽 5 · 2148m Deep · 覆冰断崖 · 轻松 · 1 小时 2 分". The
    /// clock time of the file is a save-detail fact, not a run fact, so the
    /// summary shows the played time instead.</summary>
    private static string DescribeRecent(SaveSlotMetadata save)
    {
        var parts = new List<string> { string.IsNullOrWhiteSpace(save.Label) ? save.FileName : save.Label };
        if (!string.IsNullOrWhiteSpace(save.LocationName)) parts.Add(save.LocationName);
        if (!string.IsNullOrWhiteSpace(save.LayerName)) parts.Add(save.LayerName);
        if (!string.IsNullOrWhiteSpace(save.DifficultyLabel)) parts.Add(save.DifficultyLabel);
        if (!string.IsNullOrWhiteSpace(save.CharacterLabel)) parts.Add(save.CharacterLabel);
        if (!string.IsNullOrWhiteSpace(save.RunTimeLabel)) parts.Add(save.RunTimeLabel);
        if (save.IsEmptyFile) parts.Add("空文件");
        return string.Join(" · ", parts);
    }

    private static List<SaveCandidate> EnumerateCandidates(string root, CancellationToken cancellationToken)
    {
        var results = new List<SaveCandidate>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception)
            {
                continue;
            }

            var isRoot = string.Equals(directory, root, StringComparison.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (name.StartsWith('.')) continue;
                if (IgnoredFileNames.Contains(name)) continue;
                if (!HasSaveExtension(name)) continue;
                results.Add(new SaveCandidate(file, name, isRoot));
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.')) continue;
                // casualty_reports and mp_save are app data, not the group's run
                // saves; including them made "最近保存" show mp_save's own save.sv.
                if (string.Equals(name, "casualty_reports", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "mp_save", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
        }

        results.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return results;
    }

    private static bool HasSaveExtension(string fileName) =>
        SaveExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    private static bool IsSaveFileName(string fileName) =>
        string.Equals(fileName, "save.sv", StringComparison.OrdinalIgnoreCase);

    private static bool IsAutosaveFileName(string fileName) =>
        AutosaveFilePattern.IsMatch(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>
    /// Reads QoL's <c>qol_slots.json</c>. The real file uses
    /// <c>Slots: [1, 2, …]</c> plus <c>Names: {"1": "名字"}</c>; the older
    /// scalar/array shapes are still accepted so no existing group loses slots.
    /// </summary>
    private static QolDeclaration ReadQolSlots(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > SaveGroupArchiveLimits.MaxManifestBytes) return QolDeclaration.Empty;

            using var document = JsonDocument.Parse(
                File.ReadAllText(path, Encoding.UTF8),
                new JsonDocumentOptions { MaxDepth = SaveGroupArchiveLimits.MaxManifestDepth });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return QolDeclaration.Empty;

            var slots = new SortedSet<int>();
            var slotElement = FindProperty(root, "Slots");

            if (slotElement is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (ReadInt(item) is { } number && number > 0) slots.Add(number);
                }
            }
            else if (ReadInt(slotElement) is { } count && count > 0)
            {
                for (var number = 1; number <= count; number++) slots.Add(number);
            }

            var names = new Dictionary<int, string>();
            if (FindProperty(root, "Names") is { ValueKind: JsonValueKind.Object } nameObject)
            {
                foreach (var property in nameObject.EnumerateObject())
                {
                    if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                        continue;
                    if (number <= 0) continue;
                    var text = ReadString(property.Value);
                    if (!string.IsNullOrWhiteSpace(text)) names[number] = text.Trim();
                    slots.Add(number);
                }
            }
            else if (FindProperty(root, "Names") is { ValueKind: JsonValueKind.Array } nameArray)
            {
                var index = 1;
                foreach (var item in nameArray.EnumerateArray())
                {
                    var text = ReadString(item);
                    if (!string.IsNullOrWhiteSpace(text)) names[index] = text.Trim();
                    index++;
                }
            }

            return new QolDeclaration(slots.ToList(), names);
        }
        catch (Exception)
        {
            // An unreadable declaration only means the slot count is unknown.
            return QolDeclaration.Empty;
        }
    }

    /// <summary>Depth embedded in a location label such as "2149m Deep".</summary>
    private static double? ParseDepth(string? locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName)) return null;

        var match = DepthPrefixPattern.Match(locationName);
        if (!match.Success) return null;
        return double.TryParse(
            match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var depth)
            ? depth
            : null;
    }

    private static int KindRank(SaveArtifactKind kind) => kind switch
    {
        SaveArtifactKind.Slot => 0,
        SaveArtifactKind.SingleSave => 1,
        SaveArtifactKind.Current => 2,
        SaveArtifactKind.Autosave => 3,
        _ => 4
    };

    private static string FormatDuration(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds));
        if (total < 60) return $"{total} 秒";

        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        return hours > 0 ? $"{hours} 小时 {minutes} 分" : $"{minutes} 分";
    }

    private static string HashFile(string path, out long size)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > SaveGroupArchiveLimits.MaxSaveFileBytes)
                throw new InvalidDataException("存档文件超过大小上限，未读取。");
            hash.AppendData(buffer, 0, read);
        }

        size = total;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static DateTimeOffset? SafeLastWriteTime(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static JsonElement? ReadProperty(JsonElement payload, JsonElement root, params string[] names) =>
        FindProperty(payload, names) ?? FindProperty(root, names);

    private static JsonElement? FindProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static double? ReadDouble(JsonElement? element)
    {
        if (element is not { } value) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static int? ReadInt(JsonElement? element)
    {
        if (ReadDouble(element) is not { } number) return null;
        if (number is < int.MinValue or > int.MaxValue) return null;
        return (int)Math.Round(number);
    }

    private static long? ReadLong(JsonElement? element)
    {
        if (element is { ValueKind: JsonValueKind.String } text && long.TryParse(text.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        if (ReadDouble(element) is not { } number) return null;
        return (long)Math.Round(number);
    }

    private static bool? ReadBool(JsonElement? element)
    {
        if (element is not { } value) return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetDouble(out var number) => Math.Abs(number) > double.Epsilon,
            _ => null
        };
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement? element)
    {
        if (element is not { } value) return null;

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                TimestampStyles(value.GetString()!),
                out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number > 0)
        {
            return number > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)number)
                : DateTimeOffset.FromUnixTimeSeconds((long)number);
        }

        return null;
    }

    /// <summary>
    /// The game writes local wall-clock timestamps ("2026/11/14 23:00"); only
    /// strings that actually carry a zone are treated as UTC. Parsing local
    /// values as UTC shifted every save by the machine's offset.
    /// </summary>
    private static DateTimeStyles TimestampStyles(string text)
    {
        var trimmed = text.Trim();
        var hasZone = trimmed.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                      || Regex.IsMatch(trimmed, @"[+-]\d{2}:?\d{2}$");

        return hasZone
            ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
            : DateTimeStyles.AssumeLocal;
    }

    private readonly record struct Claim(SaveArtifactKind Kind, int? SlotNumber);

    private sealed record QolDeclaration(IReadOnlyList<int> Slots, IReadOnlyDictionary<int, string> Names)
    {
        public static QolDeclaration Empty { get; } = new([], new Dictionary<int, string>());
    }

    private sealed record SaveCandidate(string Path, string FileName, bool IsRoot);
}

/// <summary>
/// The game's Chinese language table, loaded from
/// <c>&lt;game&gt;/CasualtiesUnknown_Data/Lang/zh-CN.json</c>. Missing files or
/// keys fall back to built-in Chinese labels so the UI never shows raw keys.
/// </summary>
internal sealed class SaveLanguage
{
    private static readonly string[] LangRelativePaths =
    [
        Path.Combine("CasualtiesUnknown_Data", "Lang", "zh-CN.json"),
        Path.Combine("Lang", "zh-CN.json")
    ];

    private readonly Dictionary<string, string> _entries;

    private SaveLanguage(Dictionary<string, string> entries) => _entries = entries;

    public static SaveLanguage Load(string? gameDirectory)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            return new SaveLanguage(entries);

        foreach (var relativePath in LangRelativePaths)
        {
            var path = Path.Combine(gameDirectory, relativePath);
            if (!File.Exists(path)) continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (document.RootElement.ValueKind != JsonValueKind.Object) break;

                // The game nests keys ("other": { "layertitle1": ... }); flatten
                // them to "/other/layertitle1" so lookups by path work.
                foreach (var property in document.RootElement.EnumerateObject())
                    AddEntry(entries, property.Name, property.Value);
            }
            catch (Exception)
            {
                // Invalid language file: keep the built-in fallbacks.
            }

            break;
        }

        return new SaveLanguage(entries);
    }

    public string LayerNameOrFallback(int layerIndex) =>
        Get($"/other/layertitle{layerIndex + 1}") ?? $"第 {layerIndex + 1} 层";

    public string PresetLabel(string key) =>
        Get($"/other/preset{key}") ?? SaveDifficultyPresets.LabelFor(key);

    private static readonly string[] CharacterKeys = ["experiment", "milky", "dune"];

    private static readonly string[] CharacterFallbacks = ["Experiment", "Milky", "Dune"];

    /// <summary>
    /// The save stores the chosen character as 0/1/2. Anything else keeps its raw
    /// value in the label so a modded index is visible instead of guessed.
    /// </summary>
    public string CharacterLabel(double value)
    {
        var rounded = (int)Math.Round(value);
        if (Math.Abs(value - rounded) < 0.0001 && rounded is >= 0 and <= 2)
            return Get($"/other/{CharacterKeys[rounded]}") ?? CharacterFallbacks[rounded];

        var raw = value.ToString("0.###", CultureInfo.InvariantCulture);
        return $"未知角色（{raw}）";
    }

    private string? Get(string key)
    {
        if (_entries.TryGetValue(key, out var value)) return value;
        if (key.StartsWith('/') && _entries.TryGetValue(key[1..], out var withoutSlash)) return withoutSlash;
        return null;
    }

    private static void AddEntry(Dictionary<string, string> entries, string key, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // A localized value object stops here; a namespace object recurses.
            var text = ReadText(element);
            if (text is not null)
            {
                entries[key] = text;
                return;
            }

            foreach (var child in element.EnumerateObject())
                AddEntry(entries, $"{key}/{child.Name}", child.Value);
            return;
        }

        if (element.ValueKind is JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        var leaf = ReadText(element);
        if (leaf is not null) entries[key] = leaf;
    }

    private static string? ReadText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in new[] { "zh-CN", "zh", "zh-Hans", "default", "en" })
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }
}
