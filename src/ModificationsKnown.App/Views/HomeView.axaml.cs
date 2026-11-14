using Avalonia.Controls;

namespace ModificationsKnown.App.Views;

public partial class HomeView : UserControl
{
    private const double WideBreakpoint = 1150;

    public HomeView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
    }

    private void ApplyLayout(double width)
    {
        var narrow = width < WideBreakpoint;

        if (narrow)
        {
            DashboardGrid.ColumnDefinitions = new ColumnDefinitions("*,*");
            DashboardGrid.RowDefinitions = new RowDefinitions("Auto,Auto");

            Grid.SetRow(ChecklistCard, 0);
            Grid.SetColumn(ChecklistCard, 0);
            Grid.SetColumnSpan(ChecklistCard, 2);
            Grid.SetRowSpan(ChecklistCard, 1);

            Grid.SetRow(ActivityCard, 1);
            Grid.SetColumn(ActivityCard, 0);
            Grid.SetColumnSpan(ActivityCard, 1);

            Grid.SetRow(DataCard, 1);
            Grid.SetColumn(DataCard, 1);
            Grid.SetColumnSpan(DataCard, 1);
        }
        else
        {
            DashboardGrid.ColumnDefinitions = new ColumnDefinitions("2*,*");
            DashboardGrid.RowDefinitions = new RowDefinitions("Auto,Auto");

            Grid.SetRow(ChecklistCard, 0);
            Grid.SetColumn(ChecklistCard, 0);
            Grid.SetColumnSpan(ChecklistCard, 1);
            Grid.SetRowSpan(ChecklistCard, 2);

            Grid.SetRow(ActivityCard, 0);
            Grid.SetColumn(ActivityCard, 1);
            Grid.SetColumnSpan(ActivityCard, 1);

            Grid.SetRow(DataCard, 1);
            Grid.SetColumn(DataCard, 1);
            Grid.SetColumnSpan(DataCard, 1);
        }
    }
}
