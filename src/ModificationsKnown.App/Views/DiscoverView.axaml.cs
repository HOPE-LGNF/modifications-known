using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ModificationsKnown.App.ViewModels;

namespace ModificationsKnown.App.Views;

public partial class DiscoverView : UserControl
{
    public DiscoverView()
    {
        InitializeComponent();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is DiscoverViewModel viewModel)
        {
            viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnModSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not DiscoverViewModel viewModel) return;
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not ModCardViewModel card) return;

        viewModel.SelectModCommand.Execute(card);
    }

    private async void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiscoverViewModel viewModel) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new VersionPickerDialog(viewModel);
        await dialog.ShowDialog(owner);
    }
}
