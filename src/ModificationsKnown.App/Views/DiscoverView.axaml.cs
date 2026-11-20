using Avalonia;
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

    private Point _descriptionDragStart;
    private bool _descriptionPointerDown;
    private string _descriptionDragText = string.Empty;

    private void OnDescriptionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _descriptionPointerDown = true;
        _descriptionDragStart = e.GetPosition(this);
        _descriptionDragText = (sender as SelectableTextBlock)?.SelectedText ?? string.Empty;
    }

    private async void OnDescriptionPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_descriptionPointerDown || sender is not SelectableTextBlock text) return;
        if (!e.GetCurrentPoint(text).Properties.IsLeftButtonPressed)
        {
            _descriptionPointerDown = false;
            return;
        }

        var position = e.GetPosition(this);
        var dx = position.X - _descriptionDragStart.X;
        var dy = position.Y - _descriptionDragStart.Y;
        if ((dx * dx) + (dy * dy) < 36) return;

        var selected = text.SelectedText;
        if (string.IsNullOrWhiteSpace(selected)
            || !string.Equals(selected, _descriptionDragText, StringComparison.Ordinal))
            return;

        _descriptionPointerDown = false;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(selected));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    private void OnDescriptionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _descriptionPointerDown = false;
        _descriptionDragText = string.Empty;
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

    private async void OnImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiscoverViewModel viewModel || viewModel.DetailImage is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var caption = viewModel.SelectedCard?.Name ?? "模组图片";
        var viewer = new ImageLightboxWindow(viewModel.DetailImage, caption);
        await viewer.ShowDialog(owner);
    }
}
