using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ModificationsKnown.App.ViewModels;

namespace ModificationsKnown.App.Views;

public partial class SavesView : UserControl
{
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

        item.ToggleExpandedCommand.Execute(null);
        e.Handled = true;
    }
}
