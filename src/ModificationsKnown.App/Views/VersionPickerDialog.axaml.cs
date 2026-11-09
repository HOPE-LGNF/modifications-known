using Avalonia.Input;
using Avalonia.Interactivity;
using ModificationsKnown.App.ViewModels;

namespace ModificationsKnown.App.Views;

public partial class VersionPickerDialog : DialogWindow
{
    public VersionPickerDialog()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
    }

    public VersionPickerDialog(DiscoverViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiscoverViewModel viewModel) return;

        // DownloadAsync pins the selected card/file/version before its first
        // await, so closing the picker here cannot change what gets installed.
        viewModel.DownloadCommand.Execute(null);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
