using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ModificationsKnown.App.ViewModels;

namespace ModificationsKnown.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

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

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        var paths = GetArchivePaths(e);
        if (paths.Count > 0 && DataContext is ShellViewModel shell)
            await shell.InstallDroppedArchivesAsync(paths);
    }
}
