using Avalonia.Controls;
using Avalonia.Input;
using ModificationsKnown.App.ViewModels;

namespace ModificationsKnown.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
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
