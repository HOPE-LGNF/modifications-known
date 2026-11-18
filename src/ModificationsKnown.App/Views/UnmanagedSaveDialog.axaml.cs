using Avalonia.Input;
using Avalonia.Interactivity;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.Views;

/// <summary>
/// First-run decision for real, unmanaged game saves. The receiving
/// installation is chosen here, because the game writes one global LocalLow
/// directory and the save files carry no hint about which copy they belong to.
/// Closing the dialog any other way returns null ("change nothing").
/// </summary>
public partial class UnmanagedSaveDialog : DialogWindow
{
    private readonly IReadOnlyList<UnmanagedSaveCandidate> _candidates = [];

    public UnmanagedSaveDialog()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
    }

    public UnmanagedSaveDialog(
        string liveDirectory,
        int fileCount,
        IReadOnlyList<UnmanagedSaveCandidate> candidates,
        string? selectedInstallationKey)
        : this()
    {
        _candidates = candidates;
        SummaryText.Text =
            $"检测到 {fileCount} 个未托管的存档文件。MK 会用存档组隔离这个目录，请选择如何处理现有文件；" +
            "取消不会修改任何文件，也不会启动游戏。";
        DirectoryText.Text = liveDirectory;

        InstanceCombo.ItemsSource = candidates
            .Select(candidate => $"{candidate.DisplayName} — {candidate.GroupCountLabel}")
            .ToList();

        var index = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!string.Equals(candidates[i].InstallationKey, selectedInstallationKey,
                    StringComparison.OrdinalIgnoreCase)) continue;
            index = i;
            break;
        }

        InstanceCombo.SelectedIndex = index >= 0 ? index : candidates.Count > 0 ? 0 : -1;
        InstanceCombo.SelectionChanged += (_, _) => UpdateInstanceHint();
        UpdateInstanceHint();
        Opened += (_, _) => ImportButton.Focus();
    }

    private UnmanagedSaveCandidate? Selected =>
        InstanceCombo.SelectedIndex >= 0 && InstanceCombo.SelectedIndex < _candidates.Count
            ? _candidates[InstanceCombo.SelectedIndex]
            : null;

    private void UpdateInstanceHint()
    {
        var selected = Selected;
        EmptyButton.IsEnabled = selected?.CanCreateEmpty ?? false;

        InstanceHintText.Text = selected is null
            ? "没有可选的版本；请先到设置里添加游戏实例。"
            : selected.CanCreateEmpty
                ? $"{selected.GameDirectory}\n导入会把现有存档复制成「默认」；新建空组则不复制文件。"
                : $"{selected.GameDirectory}\n该版本已有 {selected.GroupCount} 组，导入会新建「导入的存档」；新建空组请到存档管理。";
    }

    private void OnImportClick(object? sender, RoutedEventArgs e) =>
        Close(new UnmanagedSaveDecision(UnmanagedSaveChoice.Import, Selected?.InstallationKey));

    private void OnEmptyClick(object? sender, RoutedEventArgs e) =>
        Close(new UnmanagedSaveDecision(UnmanagedSaveChoice.CreateEmpty, Selected?.InstallationKey));

    private void OnSettingsClick(object? sender, RoutedEventArgs e) =>
        Close(new UnmanagedSaveDecision(UnmanagedSaveChoice.OpenInstanceSettings));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close(null);
    }
}
