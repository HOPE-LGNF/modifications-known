using Avalonia;
using ModificationsKnown.App.Services;

namespace ModificationsKnown.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ConsoleSupport.Initialize(args);
        var nxm = FindNxmArgument(args);

        using var singleInstance = new SingleInstanceService();
        if (!singleInstance.IsFirstInstance)
        {
            if (!string.IsNullOrWhiteSpace(nxm))
                singleInstance.TryForward(nxm, TimeSpan.FromSeconds(5));
            return;
        }

        singleInstance.StartServer();
        App.SingleInstance = singleInstance;
        App.StartupNxmLink = nxm;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static string? FindNxmArgument(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--nxm", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
                return args[index + 1];

            if (args[index].StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                return args[index];
        }

        return null;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
