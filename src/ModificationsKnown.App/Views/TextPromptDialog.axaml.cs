using Avalonia.Input;
using Avalonia.Interactivity;

namespace ModificationsKnown.App.Views;

public partial class TextPromptDialog : DialogWindow
{
    public TextPromptDialog()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
    }

    public TextPromptDialog(string title, string message, string initialValue, string watermark)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ValueBox.Watermark = watermark;
        ValueBox.Text = initialValue;
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        var value = ValueBox.Text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            ErrorText.Text = "名称不能为空。";
            ErrorText.IsVisible = true;
            ValueBox.Focus();
            return;
        }

        Close(value);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close(null);
    }
}
