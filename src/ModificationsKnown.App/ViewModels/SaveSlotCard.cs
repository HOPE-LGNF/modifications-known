using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.ViewModels;

public sealed record SaveFact(string Text, string? Tip = null);

/// <summary>One save inside a group, formatted for the detail cards.</summary>
public sealed class SaveSlotCard
{
    public SaveSlotCard(SaveSlotMetadata save)
    {
        Save = save;
        Label = string.IsNullOrWhiteSpace(save.Label) ? save.FileName : save.Label;
        Badges = BuildBadges(save);
        Facts = BuildFacts(save);
        FileLine = $"{save.FileName} · {FormatSize(save.FileSize)}";
    }

    public SaveSlotMetadata Save { get; }

    public string Label { get; }

    public IReadOnlyList<string> Badges { get; }

    public IReadOnlyList<SaveFact> Facts { get; }

    public string FileLine { get; }

    public bool IsReadable => Save.IsReadable;

    public bool IsUnreadable => !Save.IsReadable;

    public bool HasError => !string.IsNullOrWhiteSpace(Save.Error);

    public string Error => Save.Error ?? string.Empty;

    private static IReadOnlyList<string> BuildBadges(SaveSlotMetadata save)
    {
        var badges = new List<string> { KindLabel(save.Kind) };
        if (save.IsEmptyFile) badges.Add("空文件");
        if (save.IsMostRecent) badges.Add("最近");
        if (save.IsMultiplayer) badges.Add("多人");
        if (save.Cheated) badges.Add("已修改");
        if (save.ModCount > 0) badges.Add($"模组 {save.ModCount}");
        return badges;
    }

    private static IReadOnlyList<SaveFact> BuildFacts(SaveSlotMetadata save)
    {
        var facts = new List<SaveFact>();

        // 深度置顶：LocationName 里的“2158m Deep”与深度是同一份数据，只保留深度。
        if (save.DepthMeters is > 0) facts.Add(new($"深度：{save.DepthMeters:0} m"));
        if (!string.IsNullOrWhiteSpace(save.LayerName)) facts.Add(new($"层级：{save.LayerName}"));
        else if (save.LayerIndex is { } layer) facts.Add(new($"层级：第 {layer + 1} 层"));
        if (!string.IsNullOrWhiteSpace(save.DifficultyLabel))
        {
            var tip = string.Equals(save.DifficultyKey, SaveDifficultyPresets.CustomKey, StringComparison.Ordinal)
                ? SaveDifficultyPresets.CustomDescription
                : null;
            facts.Add(new($"难度：{save.DifficultyLabel}", tip));
        }
        if (!string.IsNullOrWhiteSpace(save.CharacterLabel)) facts.Add(new($"角色：{save.CharacterLabel}"));
        if (!string.IsNullOrWhiteSpace(save.RunTimeLabel)) facts.Add(new($"游玩：{save.RunTimeLabel}"));
        if (!string.IsNullOrWhiteSpace(save.SavedAtLabel)) facts.Add(new($"保存：{save.SavedAtLabel}"));
        if (save.CharacterId is { } characterId) facts.Add(new($"角色 ID：{characterId}"));
        if (save.SeedValue is { } seed) facts.Add(new($"种子：{seed}"));
        if (!string.IsNullOrWhiteSpace(save.SaveKind)) facts.Add(new($"类型：{save.SaveKind}"));
        if (facts.Count == 0 && save.IsReadable) facts.Add(new("无更多元数据"));
        return facts;
    }

    private static string KindLabel(SaveArtifactKind kind) => kind switch
    {
        SaveArtifactKind.Slot => "槽位",
        SaveArtifactKind.Current => "当前进度",
        SaveArtifactKind.Autosave => "自动存档",
        SaveArtifactKind.SingleSave => "原版存档",
        _ => "文件"
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };
}
