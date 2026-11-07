using System.Runtime.InteropServices;
using Microsoft.Win32;
using ModificationsKnown.Core.Common;

namespace ModificationsKnown.Infrastructure.Windows;

public sealed record NxmProtocolStatus(
    bool Supported,
    bool RegisteredForCurrentExecutable,
    string? CurrentCommand);

/// <summary>
/// Per-user registration for nxm:// links. It deliberately refuses to replace a
/// handler owned by another application; the user must choose/remove it first.
/// </summary>
public sealed class NxmProtocolRegistrar
{
    private const string ProtocolKey = @"Software\Classes\nxm";
    private const string ExpectedArguments = "--nxm \"%1\"";

    public bool Supported => OperatingSystem.IsWindows();

    public NxmProtocolStatus GetStatus()
    {
        if (!OperatingSystem.IsWindows()) return new NxmProtocolStatus(false, false, null);

        var command = ReadCommand();
        var expected = ExpectedCommand();
        return new NxmProtocolStatus(
            true,
            !string.IsNullOrWhiteSpace(command)
            && string.Equals(command, expected, StringComparison.OrdinalIgnoreCase),
            command);
    }

    public OperationResult Register(bool overwriteExisting = false)
    {
        if (!OperatingSystem.IsWindows()) return OperationResult.Fail("NXM 协议注册只支持 Windows。");

        var executable = ExecutablePath();
        if (string.IsNullOrWhiteSpace(executable)
            || !File.Exists(executable)
            || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail("无法定位当前启动器 exe，不能注册 NXM；请使用发布版 exe，而不是通过 dotnet 运行。");

        var expected = ExpectedCommand();
        var existing = ReadCommand();
        var existingIsOwnShape = !string.IsNullOrWhiteSpace(existing)
                                 && existing.EndsWith(ExpectedArguments, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existing)
            && !string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
        {
            if (!overwriteExisting)
            {
                return OperationResult.Fail(
                    $"nxm:// 已被占用：{existing}。确认后可以覆盖；其他程序的关联会先备份。");
            }
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ProtocolKey)
                            ?? throw new InvalidOperationException("无法创建 HKCU nxm 注册表项。");

            if (!string.IsNullOrWhiteSpace(existing)
                && !existingIsOwnShape
                && !string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue("MKPreviousCommand", existing);
                var previousIcon = ReadIcon();
                if (!string.IsNullOrWhiteSpace(previousIcon))
                    key.SetValue("MKPreviousIcon", previousIcon);
            }
            else
            {
                key.DeleteValue("MKPreviousCommand", throwOnMissingValue: false);
                key.DeleteValue("MKPreviousIcon", throwOnMissingValue: false);
            }
            key.SetValue(string.Empty, "URL:NXM Protocol");
            key.SetValue("URL Protocol", string.Empty);

            using (var icon = key.CreateSubKey("DefaultIcon"))
                icon.SetValue(string.Empty, $"\"{executable}\",0");

            using (var command = key.CreateSubKey(@"shell\open\command"))
                command.SetValue(string.Empty, expected);

            NotifyShell();
            return OperationResult.Ok("已注册 nxm:// 到当前启动器。");
        }
        catch (Exception exception)
        {
            return OperationResult.Fail($"注册 NXM 协议失败：{exception.Message}");
        }
    }

    public OperationResult Unregister()
    {
        if (!OperatingSystem.IsWindows()) return OperationResult.Fail("NXM 协议注册只支持 Windows。");

        var existing = ReadCommand();
        if (string.IsNullOrWhiteSpace(existing))
            return OperationResult.Ok("nxm:// 当前未注册。");

        if (!string.Equals(existing, ExpectedCommand(), StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail($"nxm:// 当前由其他程序处理，未取消：{existing}");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProtocolKey, writable: true);
            var previousCommand = key?.GetValue("MKPreviousCommand")?.ToString();
            if (!string.IsNullOrWhiteSpace(previousCommand))
            {
                using (var command = key!.CreateSubKey(@"shell\open\command"))
                    command.SetValue(string.Empty, previousCommand);

                var previousIcon = key.GetValue("MKPreviousIcon")?.ToString();
                if (!string.IsNullOrWhiteSpace(previousIcon))
                {
                    using var icon = key.CreateSubKey("DefaultIcon");
                    icon.SetValue(string.Empty, previousIcon);
                }

                key.DeleteValue("MKPreviousCommand", throwOnMissingValue: false);
                key.DeleteValue("MKPreviousIcon", throwOnMissingValue: false);
                NotifyShell();
                return OperationResult.Ok("已取消当前关联，并恢复之前的 NXM handler。");
            }

            Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);
            NotifyShell();
            return OperationResult.Ok("已取消 nxm:// 关联。");
        }
        catch (Exception exception)
        {
            return OperationResult.Fail($"取消 NXM 协议失败：{exception.Message}");
        }
    }

    private static string ExpectedCommand() =>
        $"\"{ExecutablePath()}\" {ExpectedArguments}";

    private static string? ExecutablePath() => Environment.ProcessPath;

    private static string? ReadIcon()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\DefaultIcon");
            return key?.GetValue(string.Empty)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadCommand()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command");
            return key?.GetValue(string.Empty)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0;
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }
}
