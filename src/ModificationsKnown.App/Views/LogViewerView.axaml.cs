using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ModificationsKnown.App.Views;

public partial class LogViewerView : UserControl
{
    private double _headerInset = -1;

    public LogViewerView()
    {
        InitializeComponent();
        EntriesList.LayoutUpdated += OnEntriesLayoutUpdated;
    }

    /// <summary>
    /// Keeps the sticky header aligned with the row content. When the list's
    /// vertical scrollbar takes layout width, the header receives the same
    /// right inset; overlay scrollbars report no width difference, so nothing
    /// moves in that theme. No fixed scrollbar constants, so DPI changes are
    /// handled by the same measurement.
    /// </summary>
    private void OnEntriesLayoutUpdated(object? sender, EventArgs e)
    {
        var scrollViewer = EntriesList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is null || scrollViewer.Viewport.Width <= 0)
            return;

        var inset = Math.Round(Math.Max(0, EntriesList.Bounds.Width - scrollViewer.Viewport.Width));
        if (inset > 64)
            return;

        if (Math.Abs(inset - _headerInset) < 0.5)
            return;

        _headerInset = inset;
        ListHeader.Margin = new Thickness(0, 0, inset, 0);
    }
}
