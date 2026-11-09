using Avalonia.Controls;
using ModificationsKnown.App.ViewModels;
using ModificationsKnown.Core.Mods;

namespace ModificationsKnown.App.Views;

public partial class InstalledView : UserControl
{
    // Restoring the ComboBox after a cancelled switch fires SelectionChanged
    // again; that echo must not re-enter the confirmation dialog.
    private bool _restoringGroupSelection;

    public InstalledView()
    {
        InitializeComponent();
    }

    private async void OnGroupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_restoringGroupSelection) return;
        if (DataContext is not InstalledViewModel viewModel) return;
        if (viewModel.IsReloadingGroups) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ModGroup group) return;
        if (viewModel.SelectedGroup?.Id == group.Id) return;

        var accepted = await viewModel.TrySelectGroupAsync(group);
        if (accepted) return;

        _restoringGroupSelection = true;
        try
        {
            if (sender is ComboBox combo)
                combo.SelectedItem = viewModel.SelectedGroup;
        }
        finally
        {
            _restoringGroupSelection = false;
        }
    }
}
