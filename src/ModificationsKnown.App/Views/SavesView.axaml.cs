using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModificationsKnown.App.ViewModels;

namespace ModificationsKnown.App.Views;

public partial class SavesView : UserControl
{
    private SavesViewModel? _viewModel;
    private int _scrollRetry;

    public SavesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A save group expands on a click anywhere in its card, except on the
    /// buttons (select, menu, chevron) and inside a slot card: those keep their
    /// own behaviour, and selection is now only the “选择存档组” button's job.
    /// </summary>
    private void OnGroupRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border rowBorder || rowBorder.DataContext is not SaveGroupRow row) return;
        if (e.GetCurrentPoint(rowBorder).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;

        for (var current = e.Source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, rowBorder)) break;

            switch (current)
            {
                case Button:
                case MenuItem:
                case Menu:
                    return;
                case Border border when border.Classes.Contains("save-card"):
                    return;
            }
        }

        row.ToggleExpandedCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Same click-to-expand behaviour for an installation header.</summary>
    private void OnInstallationHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border header || header.DataContext is not SaveInstallationItem item) return;
        if (e.GetCurrentPoint(header).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;

        for (var current = e.Source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, header)) break;

            switch (current)
            {
                case Button:
                case MenuItem:
                case Menu:
                    return;
            }
        }

        var wasExpanded = item.IsExpanded;
        item.ToggleExpandedCommand.Execute(null);

        // Collapsing should not move the page; expanding is the moment the user
        // wants the chosen installation aligned to the top of the viewport.
        if (!wasExpanded && item.IsExpanded)
            ScheduleScrollToCardFor(header);

        e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindViewModel(DataContext as SavesViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindViewModel(DataContext as SavesViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        BindViewModel(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void BindViewModel(SavesViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SavesViewModel.ActiveInstallationKey)) return;

        // Let the ItemsControl finish measuring/arranging the refreshed cards
        // before translating their position into a scroll offset.
        Dispatcher.UIThread.Post(ScrollToActiveInstallation, DispatcherPriority.Loaded);
    }

    private void ScrollToActiveInstallation()
    {
        if (InstancesItems is null || InstancesScroll is null) return;

        var active = InstancesItems.ItemsSource?
            .OfType<SaveInstallationItem>()
            .FirstOrDefault(item => item.IsActive);
        if (active is null) return;

        var card = FindCard(active);
        if (card is null)
        {
            // A newly rebuilt ItemsControl may realize templates one layout
            // pass later. Retry a couple of times, then give up quietly.
            if (_scrollRetry++ < 2)
                Dispatcher.UIThread.Post(ScrollToActiveInstallation, DispatcherPriority.Background);
            return;
        }

        _scrollRetry = 0;
        ScrollToCard(card);
    }

    private void ScheduleScrollToCardFor(Visual origin)
    {
        var card = origin.GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("card"));
        if (card is null) return;

        Dispatcher.UIThread.Post(() => ScrollToCard(card), DispatcherPriority.Loaded);
    }

    private Border? FindCard(SaveInstallationItem item) =>
        InstancesItems?.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border =>
                border.Classes.Contains("card")
                && ReferenceEquals(border.DataContext, item));

    private void ScrollToCard(Control card)
    {
        if (InstancesScroll is null || !card.IsAttachedToVisualTree()) return;

        var position = card.TranslatePoint(new Point(0, 0), InstancesScroll);
        if (position is null) return;

        var maxOffset = Math.Max(0,
            InstancesScroll.Extent.Height - InstancesScroll.Viewport.Height);
        var target = Math.Clamp(
            InstancesScroll.Offset.Y + position.Value.Y - 12,
            0,
            maxOffset);

        if (Math.Abs(target - InstancesScroll.Offset.Y) < 0.5) return;
        InstancesScroll.Offset = new Vector(InstancesScroll.Offset.X, target);
    }
}
