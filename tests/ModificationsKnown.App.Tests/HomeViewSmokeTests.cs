using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using ModificationsKnown.App.Views;

namespace ModificationsKnown.App.Tests;

/// <summary>
/// H1 smoke coverage: the production AXAML and its application resources must
/// initialize in a headless test host without starting the launcher shell.
/// </summary>
public sealed class HomeViewSmokeTests
{
    [AvaloniaFact]
    public void ProductionApplicationResourcesAreLoaded()
    {
        Assert.NotNull(Application.Current);
        Assert.IsType<ModificationsKnown.App.App>(Application.Current);
        Assert.True(Application.Current!.TryFindResource("AccentBrush", out var accent));
        Assert.IsType<Avalonia.Media.SolidColorBrush>(accent);

        // Proves Program.Main never ran: it assigns SingleInstance before the
        // Avalonia app starts. The wider "no settings/log/services" guarantee is
        // structural - App.axaml.cs:37 returns before constructing any service
        // when the lifetime is not a classic desktop one.
        Assert.Null(ModificationsKnown.App.App.SingleInstance);
    }

    [AvaloniaFact]
    public void HomeViewBuildsItsControlsAndContent()
    {
        var view = new HomeView();
        var window = HeadlessWindowHost.Show(view);

        Assert.NotNull(view.FindControl<Grid>("DashboardGrid"));
        Assert.NotNull(view.FindControl<Border>("ChecklistCard"));
        Assert.NotNull(view.FindControl<Border>("ActivityCard"));
        Assert.NotNull(view.FindControl<Border>("DataCard"));

        var texts = view.GetVisualDescendants().OfType<TextBlock>()
            .Select(block => block.Text)
            .ToList();
        Assert.Contains("本地模组工作台", texts);

        // Layout ran for real: the view has a measured size.
        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        HeadlessWindowHost.Close(window);
    }

    [AvaloniaFact]
    public void HomeViewPicksUpApplicationLevelStyles()
    {
        var view = new HomeView();
        var window = HeadlessWindowHost.Show(view);

        var cards = view.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("card"))
            .ToList();

        // 14 is the CornerRadius that App.axaml's Border.card style declares.
        Assert.NotEmpty(cards);
        Assert.All(cards, card => Assert.Equal(new CornerRadius(14), card.CornerRadius));

        HeadlessWindowHost.Close(window);
    }
}
