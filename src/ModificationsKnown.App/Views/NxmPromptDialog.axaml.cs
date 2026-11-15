using Avalonia.Interactivity;

namespace ModificationsKnown.App.Views;

public partial class NxmPromptDialog : DialogWindow
{
    public NxmPromptDialog()
    {
        InitializeComponent();
    }

    private void OnRegisterClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
