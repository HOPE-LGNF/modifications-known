using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModificationsKnown.App.ViewModels;

namespace ModificationsKnown.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ScheduleFocus();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.FocusRequest))
            ScheduleFocus();
    }

    private void ScheduleFocus() =>
        Dispatcher.UIThread.Post(ApplyFocus, DispatcherPriority.Loaded);

    private void ApplyFocus()
    {
        if (_viewModel is null) return;

        switch (_viewModel.RequestedFocus)
        {
            case "nxm":
                NxmSection.BringIntoView();
                break;
            case "bepinex":
                var button = VersionsList
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(candidate =>
                        candidate.DataContext is GameVersionRow { IsHighlighted: true });
                button?.BringIntoView();
                break;
        }
    }

    private void OnAliasKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: GameVersionRow row }) return;

        if (e.Key == Key.Enter)
        {
            if (row.SaveAliasCommand.CanExecute(null))
                row.SaveAliasCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.ResetAlias();
            e.Handled = true;
        }
    }
}
