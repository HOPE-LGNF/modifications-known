using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Instances;

namespace ModificationsKnown.App.Views;

/// <summary>
/// Confirms removing an instance record. Save groups are never an option here:
/// the dialog states how many groups stay behind and the only destructive
/// choice is the optional game install folder.
/// </summary>
public partial class DeleteInstanceDialog : DialogWindow
{
    private readonly GameInstallation _installation;
    private readonly IReadOnlyList<SaveGroup> _groups;
    private DispatcherTimer? _confirmTimer;
    private int _confirmSeconds;

    public DeleteInstanceDialog()
        : this(
            new GameInstallation(
                new GameProfile("placeholder", "版本", "CU", GameEdition.Full, "0", "scavprototype", "CasualtiesUnknown_Data"),
                string.Empty,
                null,
                false,
                false),
            [])
    {
    }

    public DeleteInstanceDialog(GameInstallation installation, IReadOnlyList<SaveGroup> groups)
    {
        _installation = installation;
        _groups = groups;
        InitializeComponent();

        InstanceLabel.Text = installation.DisplayLabel;
        InstancePath.Text = installation.GameDirectory;
        DeleteFolderCheck.IsChecked = false;
        SteamWarning.IsVisible = installation.GameDirectory.Contains(
            "steamapps",
            StringComparison.OrdinalIgnoreCase);

        SaveGroupsTitle.Text = groups.Count == 0
            ? "存档组：0 个（没有需要保留的组）"
            : $"存档组：{groups.Count} 个，全部保留";
        SaveGroupList.ItemsSource = groups
            .OrderByDescending(group => group.IsDefault)
            .ThenBy(group => group.CreatedAt)
            .Select(group => group.IsDefault ? $"  · {group.Name}（默认）" : $"  · {group.Name}")
            .ToList();
        SaveGroupList.IsVisible = groups.Count > 0;
        NoSaveGroupsLabel.IsVisible = groups.Count == 0;

        KeyDown += OnWindowKeyDown;
        Opened += (_, _) => DeleteFolderCheck.Focus();
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        SummaryText.Text = BuildSummary();
        StepOnePanel.IsVisible = false;
        StepTwoPanel.IsVisible = true;
        StartConfirmCountdown();
    }

    private string BuildSummary()
    {
        var lines = new List<string>
        {
            $"版本：{_installation.DisplayLabel}",
            $"目录：{_installation.GameDirectory}",
            string.Empty,
            "将删除的 MK 记录：",
            "  · 版本别名与实例记录（并标记为隐藏，刷新后不再出现）",
            "  · 该实例的 Mod 组快照",
            "  · 该实例的 Nexus 手动关联与安装来源记录",
            string.Empty,
            "存档组：",
            "  · 全部保留，不会被复制、移动、删除或重新链接"
        };

        foreach (var group in _groups.OrderByDescending(group => group.IsDefault)
                     .ThenBy(group => group.CreatedAt))
        {
            lines.Add($"      - {group.Name}{(group.IsDefault ? "（默认）" : string.Empty)}");
        }

        lines.Add(string.Empty);
        lines.Add("游戏安装目录：");
        lines.Add(DeleteFolderCheck.IsChecked == true
            ? $"  · 删除 {_installation.GameDirectory}（其中的 BepInEx、模组与日志一并删除，无法恢复）"
            : $"  · 保留 {_installation.GameDirectory}");

        return string.Join(Environment.NewLine, lines);
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        StopConfirmCountdown();
        StepTwoPanel.IsVisible = false;
        StepOnePanel.IsVisible = true;
    }

    /// <summary>Force a five-second pause on the destructive button so the
    /// final step cannot be confirmed with a double-click reflex.</summary>
    private void StartConfirmCountdown()
    {
        StopConfirmCountdown();
        _confirmSeconds = 5;
        ConfirmDeleteButton.IsEnabled = false;
        ConfirmDeleteButton.Content = $"确认删除（{_confirmSeconds}）";

        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _confirmTimer.Tick += (_, _) =>
        {
            _confirmSeconds--;
            if (_confirmSeconds > 0)
            {
                ConfirmDeleteButton.Content = $"确认删除（{_confirmSeconds}）";
                return;
            }

            StopConfirmCountdown();
            ConfirmDeleteButton.IsEnabled = true;
            ConfirmDeleteButton.Content = "确认删除";
        };
        _confirmTimer.Start();
    }

    private void StopConfirmCountdown()
    {
        _confirmTimer?.Stop();
        _confirmTimer = null;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) =>
        Close(new InstanceDeletionPlan(DeleteFolderCheck.IsChecked == true));

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        StopConfirmCountdown();
        Close(null);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (StepTwoPanel.IsVisible)
        {
            StopConfirmCountdown();
            StepTwoPanel.IsVisible = false;
            StepOnePanel.IsVisible = true;
            e.Handled = true;
            return;
        }

        Close(null);
    }
}
