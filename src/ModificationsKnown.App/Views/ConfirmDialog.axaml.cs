using Avalonia.Input;
using Avalonia.Interactivity;

namespace ModificationsKnown.App.Views;

public partial class ConfirmDialog : DialogWindow
{
    public ConfirmDialog()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
    }

    public ConfirmDialog(string title, string message, string confirmText, string cancelText, bool showCancel)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        CancelButton.IsVisible = showCancel;
        Opened += (_, _) => ConfirmButton.Focus();
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close(false);
    }
}
