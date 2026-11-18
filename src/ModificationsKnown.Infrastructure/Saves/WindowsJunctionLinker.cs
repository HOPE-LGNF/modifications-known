using System.Diagnostics;
using ModificationsKnown.Core.Saves;

namespace ModificationsKnown.Infrastructure.Saves;

/// <summary>
/// NTFS junction implementation. Junctions are directory links, so removing the
/// link never touches the profile files it points at.
/// </summary>
public sealed class WindowsJunctionLinker : ISaveLinker
{
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
        try
        {
            var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(start);
            if (process is null)
            {
                error = "无法启动 mklink。";
                return false;
            }

            process.WaitForExit(15000);
            error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode == 0 && Directory.Exists(linkPath)) return true;

            error = string.IsNullOrWhiteSpace(error) ? $"mklink 退出码 {process.ExitCode}" : error;
            return false;
        }
        catch (Exception exception)
        {
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
}
