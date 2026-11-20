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

        // Preserve an existing selection when the user presses inside it;
        // otherwise a click clears the selection before a drag handle can be used.
        DescriptionText.AddHandler(
            PointerPressedEvent,
            OnDescriptionPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
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

        var start = Math.Min(DescriptionText.SelectionStart, DescriptionText.SelectionEnd);
        var end = Math.Max(DescriptionText.SelectionStart, DescriptionText.SelectionEnd);
        if (start == end) return;

        var point = e.GetPosition(DescriptionText);
        point = new Point(
            point.X - DescriptionText.Padding.Left,
            point.Y - DescriptionText.Padding.Top);
        var hit = DescriptionText.TextLayout.HitTestPoint(point);
        if (hit.IsInside && hit.TextPosition >= start && hit.TextPosition <= end)
            e.Handled = true;
    }

    private async void OnDescriptionDragHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_descriptionPointerDown) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _descriptionPointerDown = false;
            return;
        }

        var position = e.GetPosition(this);
        var dx = position.X - _descriptionDragStart.X;
        var dy = position.Y - _descriptionDragStart.Y;
        if ((dx * dx) + (dy * dy) < 6) return;

        var selected = _descriptionDragText;
        if (string.IsNullOrWhiteSpace(selected)) return;

        _descriptionPointerDown = false;
        DescriptionDragHandle.IsVisible = false;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(selected));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    private void OnDescriptionDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _descriptionDragText = DescriptionText.SelectedText;
        if (string.IsNullOrWhiteSpace(_descriptionDragText)) return;
        _descriptionPointerDown = true;
        _descriptionDragStart = e.GetPosition(this);
    }

    private void OnDescriptionDragHandleReleased(object? sender, PointerReleasedEventArgs e) =>
        _descriptionPointerDown = false;

    private void OnDescriptionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _descriptionPointerDown = false;
        DescriptionDragHandle.IsVisible =
            !string.IsNullOrWhiteSpace(DescriptionText.SelectedText);
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
