using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.App.Views;

public partial class DeleteInstanceDialog : DialogWindow
{
    private readonly GameInstallation _installation;
    private readonly IReadOnlyList<InstanceSaveFile> _saves;
    private readonly string _profileDirectory;

    public DeleteInstanceDialog()
        : this(
            new GameInstallation(
                new GameProfile("placeholder", "版本", "CU", GameEdition.Full, "0", "scavprototype", "CasualtiesUnknown_Data"),
                string.Empty,
                null,
                false,
                false),
            [],
            string.Empty,
            string.Empty)
    {
    }

    public DeleteInstanceDialog(
        GameInstallation installation,
        IReadOnlyList<InstanceSaveFile> saves,
        string profileDirectory,
        string suggestedTarget)
    {
        _installation = installation;
        _saves = saves;
        _profileDirectory = profileDirectory;
        InitializeComponent();

        InstanceLabel.Text = installation.DisplayLabel;
        InstancePath.Text = installation.GameDirectory;
        TargetBox.Text = suggestedTarget;
        SaveList.ItemsSource = saves;
        SaveCountLabel.Text = saves.Count == 0
            ? "没有找到存档文件。"
            : $"找到 {saves.Count} 个存档文件：";
        NoSavesLabel.IsVisible = saves.Count == 0;

        KeyDown += OnWindowKeyDown;
        Opened += (_, _) => CopyRadio.Focus();
    }

    private SaveDisposition Disposition => KeepRadio.IsChecked == true
        ? SaveDisposition.Keep
        : MoveRadio.IsChecked == true
            ? SaveDisposition.Move
            : SaveDisposition.Copy;

    private void OnSaveModeChanged(object? sender, RoutedEventArgs e)
    {
        if (TargetRow is null) return;
        var keep = KeepRadio.IsChecked == true;
        TargetRow.IsEnabled = !keep;
        BrowseButton.IsEnabled = !keep;
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择存档保存目录",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            TargetBox.Text = folders[0].Path.LocalPath;
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        StepOneError.IsVisible = false;
        var disposition = Disposition;
        var target = TargetBox.Text?.Trim() ?? string.Empty;

        if (disposition is SaveDisposition.Copy or SaveDisposition.Move)
        {
            if (target.Length == 0)
            {
                ShowError(StepOneError, "请选择存档目录，或改为“保持原位”。");
                return;
            }

            try
            {
                var full = Path.GetFullPath(target);
                if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
                {
                    ShowError(StepOneError, "目标目录已存在且不为空，请换一个空目录。");
                    return;
                }

                if (IsInside(full, _profileDirectory) || IsInside(_profileDirectory, full))
                {
                    ShowError(StepOneError, "存档目录不能位于实例托管存档目录内，也不能是它的上级目录。");
                    return;
                }
            }
            catch (Exception exception)
            {
                ShowError(StepOneError, $"目录无效：{exception.Message}");
                return;
            }
        }

        SummaryText.Text = BuildSummary(disposition, target);
        StepOnePanel.IsVisible = false;
        StepTwoPanel.IsVisible = true;
    }

    private string BuildSummary(SaveDisposition disposition, string target)
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
            "存档："
        };

        switch (disposition)
        {
            case SaveDisposition.Copy:
                lines.Add($"  · 复制到 {target}");
                lines.Add("  · 复制完成后删除 MK 托管存档目录");
                break;
            case SaveDisposition.Move:
                lines.Add($"  · 移动到 {target}");
                break;
            default:
                lines.Add("  · 保持原位，不处理 MK 托管存档目录");
                break;
        }

        lines.Add(string.Empty);
        lines.Add("实例文件夹：");
        lines.Add(DeleteFolderCheck.IsChecked == true
            ? $"  · 删除 {_installation.GameDirectory}"
            : $"  · 保留 {_installation.GameDirectory}");

        return string.Join(Environment.NewLine, lines);
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        StepTwoPanel.IsVisible = false;
        StepOnePanel.IsVisible = true;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        var disposition = Disposition;
        var target = disposition == SaveDisposition.Keep
            ? null
            : TargetBox.Text?.Trim();

        Close(new InstanceDeletionPlan(
            disposition,
            string.IsNullOrWhiteSpace(target) ? null : target,
            DeleteFolderCheck.IsChecked == true));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (StepTwoPanel.IsVisible)
        {
            StepTwoPanel.IsVisible = false;
            StepOnePanel.IsVisible = true;
            e.Handled = true;
            return;
        }

        Close(null);
    }

    private static void ShowError(TextBlock target, string message)
    {
        target.Text = message;
        target.IsVisible = true;
    }

    private static bool IsInside(string path, string parent)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
