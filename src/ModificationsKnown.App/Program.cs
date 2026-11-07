using Avalonia;
using ModificationsKnown.App.Services;

namespace ModificationsKnown.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ConsoleSupport.Initialize(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
