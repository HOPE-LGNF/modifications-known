using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ModificationsKnown.App.ViewModels;

namespace ModificationsKnown.App.Views;

public partial class MainWindow : Window
{
    private bool _tabsPressed;
    private bool _tabsCaptured;
    private Point _tabsStart;
    private double _tabsStartOffset;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        TitleTabsScroll.AddHandler(PointerPressedEvent, OnTabsPointerPressed, RoutingStrategies.Tunnel, true);
        TitleTabsScroll.AddHandler(PointerMovedEvent, OnTabsPointerMoved, RoutingStrategies.Tunnel, true);
        TitleTabsScroll.AddHandler(PointerReleasedEvent, OnTabsPointerReleased, RoutingStrategies.Tunnel, true);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && MaximizeGlyph is not null)
            MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private bool TabsOverflow => TitleTabsScroll.Extent.Width > TitleTabsScroll.Viewport.Width + 1;

    private void OnTabsPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!TabsOverflow) return;

        var max = Math.Max(0, TitleTabsScroll.Extent.Width - TitleTabsScroll.Viewport.Width);
        var offset = Math.Clamp(TitleTabsScroll.Offset.X - e.Delta.Y * 70, 0, max);
        TitleTabsScroll.Offset = new Vector(offset, 0);
        e.Handled = true;
    }

    private void OnTabsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TitleTabsScroll).Properties.IsLeftButtonPressed) return;
        _tabsPressed = true;
        _tabsCaptured = false;
        _tabsStart = e.GetPosition(TitleTabsScroll);
        _tabsStartOffset = TitleTabsScroll.Offset.X;
    }

    private void OnTabsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_tabsPressed || !TabsOverflow) return;

        var x = e.GetPosition(TitleTabsScroll).X;
        if (!_tabsCaptured && Math.Abs(x - _tabsStart.X) < 8) return;

        if (!_tabsCaptured)
        {
            _tabsCaptured = true;
            e.Pointer.Capture(TitleTabsScroll);
        }

        var max = Math.Max(0, TitleTabsScroll.Extent.Width - TitleTabsScroll.Viewport.Width);
        var offset = Math.Clamp(_tabsStartOffset - (x - _tabsStart.X), 0, max);
        TitleTabsScroll.Offset = new Vector(offset, 0);
        e.Handled = true;
    }

    private void OnTabsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured == TitleTabsScroll)
            e.Pointer.Capture(null);

        _tabsPressed = false;
        _tabsCaptured = false;
    }

    private static List<string> GetArchivePaths(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? [])
        .Select(item => item.TryGetLocalPath())
        .OfType<string>()
        .Where(path => ShellViewModel.ArchiveExtensions.Contains(
            Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        .ToList();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = GetArchivePaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = e.DragEffects != DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // Moving over a child can look like a DragLeave; only hide when the
        // pointer actually left the window.
        DispatcherTimer.RunOnce(
            () =>
            {
                if (!IsPointerOver) DropOverlay.IsVisible = false;
            },
            TimeSpan.FromMilliseconds(60));
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        var paths = GetArchivePaths(e);
        if (paths.Count > 0 && DataContext is ShellViewModel shell)
            await shell.InstallDroppedArchivesAsync(paths);
    }
}