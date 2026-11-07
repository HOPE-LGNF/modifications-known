using System.Runtime.InteropServices;
using System.Text;

namespace ModificationsKnown.App.Services;

/// <summary>
/// WinExe applications do not get a console by default. A launcher test build
/// can be started with --console to allocate one, then all IAppLogger output is
/// mirrored to it by FileAppLogger.
/// </summary>
public static class ConsoleSupport
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    public static bool IsRequested(string[] args) =>
        args.Any(arg => arg.Equals("--console", StringComparison.OrdinalIgnoreCase)
                        || arg.Equals("--debug-console", StringComparison.OrdinalIgnoreCase));

    public static void Initialize(string[] args)
    {
        var requested = IsRequested(args);

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Encoding is not critical for the logger.
        }

        if (!OperatingSystem.IsWindows() || !requested) return;

        if (!AttachConsole(AttachParentProcess))
            AllocConsole();

        var stdout = Console.OpenStandardOutput();
        var stderr = Console.OpenStandardError();
        Console.SetOut(new StreamWriter(stdout, Encoding.UTF8) { AutoFlush = true });
        Console.SetError(new StreamWriter(stderr, Encoding.UTF8) { AutoFlush = true });
        Console.Title = "Modifications Known — debug console";
    }
}
