using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// NTFS junction implementation. Junctions are directory links, so removing the
/// link never touches the profile files it points at.
/// <para>
/// Links are created with <c>FSCTL_SET_REPARSE_POINT</c> instead of
/// <c>cmd.exe /c mklink</c>: a path is never parsed by a command interpreter, so
/// characters such as <c>%VAR%</c>, <c>&amp;</c> or quotes stay literal. A link
/// that cannot be read back after creation is removed again, so the caller only
/// ever sees a verified link or a failure that left nothing behind.
/// </para>
/// </summary>
public sealed class WindowsJunctionLinker : ISaveLinker
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool Exists(string path) => Directory.Exists(path);

    public bool TryGetTarget(string path, out string target)
    {
        target = string.Empty;
        try
        {
            if (!Directory.Exists(path)) return false;

            var info = new DirectoryInfo(path);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

            var link = info.LinkTarget;
            if (string.IsNullOrWhiteSpace(link)) return false;

            target = Path.GetFullPath(link);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool CreateJunction(string linkPath, string targetPath, out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "当前系统不支持 NTFS junction。";
            return false;
        }

        string link;
        string target;
        try
        {
            link = Normalize(linkPath);
            target = Normalize(targetPath);
        }
        catch (Exception exception)
        {
            error = $"存档链接路径无效：{exception.Message}";
            return false;
        }

        if (!Path.IsPathFullyQualified(target))
        {
            error = $"链接目标不是绝对路径：{targetPath}";
            return false;
        }

        if (target.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = $"junction 不能指向网络路径：{target}";
            return false;
        }

        if (string.Equals(link, target, StringComparison.OrdinalIgnoreCase))
        {
            error = "链接路径与目标路径相同。";
            return false;
        }

        if (Directory.Exists(link) || File.Exists(link))
        {
            error = $"链接路径已存在，未覆盖：{link}";
            return false;
        }

        if (!Directory.Exists(target))
        {
            error = $"链接目标目录不存在：{target}";
            return false;
        }

        var created = false;
        try
        {
            var parent = Path.GetDirectoryName(link);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            // The empty directory itself becomes the reparse point.
            Directory.CreateDirectory(link);
            created = true;

            if (!TrySetMountPoint(link, target, out error))
            {
                TryRemoveEmptyDirectory(link);
                return false;
            }

            if (TryGetTarget(link, out var actual) && PathEquals(actual, target))
            {
                error = string.Empty;
                return true;
            }

            // "Created but unverifiable" is reported as a failure and undone:
            // never leave a link behind whose target the app could not read back.
            RemoveJunction(link, out _);
            error = "链接已创建，但无法确认其目标；为避免留下不确定的链接，已移除。";
            return false;
        }
        catch (Exception exception)
        {
            if (created) TryRemoveEmptyDirectory(link);
            error = exception.Message;
            return false;
        }
    }

    public bool RemoveJunction(string linkPath, out string error)
    {
        try
        {
            if (Directory.Exists(linkPath))
            {
                var info = new DirectoryInfo(linkPath);
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    error = "该路径不是链接，未删除。";
                    return false;
                }

                Directory.Delete(linkPath, recursive: false);
            }

            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TrySetMountPoint(string link, string target, out string error)
    {
        using var handle = CreateFileW(
            link,
            GenericWrite,
            ShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = Describe("无法打开链接目录", Marshal.GetLastWin32Error());
            return false;
        }

        var buffer = BuildMountPointBuffer(@"\??\" + target, target);
        if (DeviceIoControl(
                handle,
                FsctlSetReparsePoint,
                buffer,
                buffer.Length,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
        {
            error = string.Empty;
            return true;
        }

        error = Describe("无法创建 junction", Marshal.GetLastWin32Error());
        return false;
    }

    /// <summary>
    /// REPARSE_DATA_BUFFER with the mount-point payload: the substitute name is
    /// the NT path the filter manager resolves, the print name is what Explorer
    /// and <see cref="DirectoryInfo.LinkTarget"/> show.
    /// </summary>
    private static byte[] BuildMountPointBuffer(string substituteName, string printName)
    {
        var substitute = Encoding.Unicode.GetBytes(substituteName);
        var print = Encoding.Unicode.GetBytes(printName);
        var pathBufferLength = substitute.Length + sizeof(char) + print.Length + sizeof(char);
        var reparseDataLength = 4 * sizeof(ushort) + pathBufferLength;

        var buffer = new byte[2 * sizeof(uint) + reparseDataLength];
        using var writer = new BinaryWriter(new MemoryStream(buffer), Encoding.Unicode);

        writer.Write(IoReparseTagMountPoint);
        writer.Write((ushort)reparseDataLength);
        writer.Write((ushort)0);

        writer.Write((ushort)0);
        writer.Write((ushort)substitute.Length);
        writer.Write((ushort)(substitute.Length + sizeof(char)));
        writer.Write((ushort)print.Length);

        writer.Write(substitute);
        writer.Write((ushort)0);
        writer.Write(print);
        writer.Write((ushort)0);

        return buffer;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathEquals(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Describe(string message, int errorCode) =>
        errorCode == 0
            ? message + "。"
            : $"{message}（{new Win32Exception(errorCode).Message}，错误码 {errorCode}）。";

    private static void TryRemoveEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path, recursive: false);
        }
        catch
        {
            // Best effort: the caller already reports the primary failure.
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        int inputBufferSize,
        IntPtr outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}
