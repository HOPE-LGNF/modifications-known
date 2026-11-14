using Avalonia.Interactivity;
using ModificationsKnown.App.Services;

namespace ModificationsKnown.App.Views;

public partial class UpdateModDialog : DialogWindow
{
    public UpdateModDialog()
    {
        InitializeComponent();
    }

    public UpdateModDialog(string title, string message, bool showBackupOption = true)
        : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        BackupCheck.IsVisible = showBackupOption;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) =>
        Close(new UpdateModChoice(BackupCheck.IsChecked == true));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
