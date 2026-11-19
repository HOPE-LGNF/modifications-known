using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModificationsKnown.App.Theming;
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
        var originalTheme = ThemeService.Current;
        var settings = new FakeAppSettingsService();
        var shell = new FakeAppShell(settings);
        var viewModel = new LauncherSkinViewModel(shell, settings, new FakeFilePickerService(), new FakeAppLogger());

        var view = new LauncherSkinView { DataContext = viewModel };

        await RunWithThemeCleanupAsync(view, async window =>
        {
            var applyButtons = view.GetVisualDescendants().OfType<Button>()
                .Where(button => Equals(button.Content, "应用此主题"))
                .ToList();
            Assert.Equal(viewModel.Themes.Count, applyButtons.Count);

            var targetIndex = Enumerable.Range(0, viewModel.Themes.Count)
                .First(index => viewModel.Themes[index].Id != settings.Current.ThemePreset
                                && viewModel.Themes[index].Id != originalTheme.Id);
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

            // ThemeService.Apply writes the palette into the live application
            // resources and updates the static Current theme.
            Assert.True(Application.Current!.TryFindResource("CardShadow", out _));
            Assert.Equal(target.Id, ThemeService.Current.Id);
        });

        // The guarded helper restored the static theme after closing the window.
        Assert.Equal(originalTheme.Id, ThemeService.Current.Id);
    }

    [AvaloniaFact]
    public async Task ThemeChangeIsRestoredAndWindowClosedWhenTheBodyThrows()
    {
        var originalTheme = ThemeService.Current;
        var settings = new FakeAppSettingsService();
        var shell = new FakeAppShell(settings);
        var viewModel = new LauncherSkinViewModel(shell, settings, new FakeFilePickerService(), new FakeAppLogger());
        var view = new LauncherSkinView { DataContext = viewModel };
        Window? window = null;

        var thrown = await Record.ExceptionAsync(() => RunWithThemeCleanupAsync(view, w =>
        {
            window = w;
            var other = ThemePalettes.All.First(palette => palette.Id != originalTheme.Id);
            ThemeService.Apply(other);
            Assert.NotEqual(originalTheme.Id, ThemeService.Current.Id);

            throw new InvalidOperationException("boom");
        }));

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.NotNull(window);
        Assert.False(window!.IsVisible);
        Assert.Equal(originalTheme.Id, ThemeService.Current.Id);
    }

    /// <summary>
    /// Shows the view in a headless window, then always closes it and restores
    /// the static theme. A cleanup failure must not hide the original test
    /// exception; when the body succeeded it is still reported.
    /// </summary>
    private static async Task RunWithThemeCleanupAsync(LauncherSkinView view, Func<Window, Task> body)
    {
        var original = ThemeService.Current;
        var bodyFailed = false;
        Window? window = null;

        try
        {
            window = HeadlessWindowHost.Show(view, width: 1400, height: 1000);
            await body(window);
        }
        catch
        {
            bodyFailed = true;
            throw;
        }
        finally
        {
            Exception? cleanupError = null;

            if (window is not null)
            {
                try
                {
                    HeadlessWindowHost.Close(window);
                }
                catch (Exception exception)
                {
                    cleanupError = exception;
                }
            }

            try
            {
                ThemeService.Apply(original);
            }
            catch (Exception exception)
            {
                cleanupError ??= exception;
            }

            if (!bodyFailed && cleanupError is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupError).Throw();
            }
        }
    }
}
