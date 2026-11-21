using Avalonia.Controls;
using Avalonia.Threading;

namespace ModificationsKnown.App.Tests;

/// <summary>
/// Shared window lifecycle for headless view tests: show the control in a
/// headless window, flush the dispatcher so layout actually runs, and close it
/// again. Sizes are the only per-test input.
/// </summary>
internal static class HeadlessWindowHost
{
    public const double DefaultWidth = 1300;

    public const double DefaultHeight = 800;

    public static Window Show(Control content, double width = DefaultWidth, double height = DefaultHeight)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    public static void Close(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }
}
