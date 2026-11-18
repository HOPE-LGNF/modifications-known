using Avalonia.Input;
using Avalonia.Interactivity;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.Views;

/// <summary>
/// First-run decision for real, unmanaged game saves. Closing the dialog any
/// other way returns null, which the caller treats as "change nothing".
/// </summary>
public partial class UnmanagedSaveDialog : DialogWindow
{
    public UnmanagedSaveDialog()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
    }

    public UnmanagedSaveDialog(string liveDirectory, int fileCount)
        : this()
    {
        SummaryText.Text =
            $"检测到 {fileCount} 个未托管的存档文件。MK 会用存档组隔离这个目录，请选择如何处理现有文件；" +
            "取消不会修改任何文件，也不会启动游戏。";
        DirectoryText.Text = liveDirectory;
        Opened += (_, _) => ImportButton.Focus();
    }

    private void OnImportClick(object? sender, RoutedEventArgs e) =>
        Close(UnmanagedSaveAction.Import);

    private void OnEmptyClick(object? sender, RoutedEventArgs e) =>
        Close(UnmanagedSaveAction.CreateEmpty);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close(null);
    }
}
