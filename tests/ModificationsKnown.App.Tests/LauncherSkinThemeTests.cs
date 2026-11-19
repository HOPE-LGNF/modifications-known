using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModificationsKnown.App.ViewModels;
using ModificationsKnown.App.Views;

namespace ModificationsKnown.App.Tests;

/// <summary>
/// H2 coverage: a real production view drives a real command and the result is
/// observable on the view model, the injected boundaries and the application
/// resources. Nothing here touches settings files, the process list or pickers.
///
/// Not covered: hover, keyboard focus, the physical hit area and colours/pixels.
/// Avalonia.Headless does not promise them; they stay part of manual UI review.
/// </summary>
public sealed class LauncherSkinThemeTests
{
    [AvaloniaFact]
    public async Task ClickingApplyOnThemeCardSavesPresetAndAppliesTheme()
    {
        var settings = new FakeAppSettingsService();
        var shell = new FakeAppShell(settings);
        var viewModel = new LauncherSkinViewModel(shell, settings, new FakeFilePickerService(), new FakeAppLogger());

        var view = new LauncherSkinView { DataContext = viewModel };
        var window = HeadlessWindowHost.Show(view, width: 1400, height: 1000);

        var applyButtons = view.GetVisualDescendants().OfType<Button>()
            .Where(button => Equals(button.Content, "应用此主题"))
            .ToList();
        Assert.Equal(viewModel.Themes.Count, applyButtons.Count);

        var targetIndex = Enumerable.Range(0, viewModel.Themes.Count)
            .First(index => viewModel.Themes[index].Id != settings.Current.ThemePreset);
        var target = viewModel.Themes[targetIndex];
        Assert.Same(target.ApplyCommand, applyButtons[targetIndex].Command);

        // Nothing has been saved or themed yet.
        Assert.Equal(0, settings.SaveCount);
        Assert.False(Application.Current!.TryFindResource("CardShadow", out _));

        var button = applyButtons[targetIndex];
        var center = button.TranslatePoint(
            new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        Assert.NotNull(center);

        window.MouseDown(center!.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        if (target.ApplyCommand.ExecutionTask is { } running)
            await running;

        Assert.Equal(target.Id, settings.Current.ThemePreset);
        Assert.Equal(1, settings.SaveCount);
        Assert.Contains(target.Name, shell.LastStatus);
        Assert.Equal(ModificationsKnown.App.Services.StatusKind.Success, shell.LastStatusKind);
        Assert.Equal(target.Name, viewModel.CurrentThemeName);
        Assert.True(target.IsSelected);

        // ThemeService.Apply writes the palette into the live application resources.
        Assert.True(Application.Current!.TryFindResource("CardShadow", out _));

        HeadlessWindowHost.Close(window);
    }
}
