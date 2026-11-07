using System.Diagnostics;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.Infrastructure.Games;

public sealed class GameLauncher : IGameLauncher
{
    private readonly IAppLogger? _logger;

    public GameLauncher(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public Task<OperationResult> LaunchViaSteamAsync(
        GameProfile profile,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var uri = $"steam://rungameid/{profile.SteamAppId}";
                _logger?.Info($"Launching via Steam: {uri}");
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return OperationResult.Ok($"已发送 Steam 启动命令：{profile.EditionLabel}。");
            }
            catch (Exception exception)
            {
                return OperationResult.Fail($"启动失败：{exception.Message}");
            }
        }, cancellationToken);
    }

    public Task<OperationResult> LaunchDirectAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(installation.ExecutablePath)
                || !File.Exists(installation.ExecutablePath))
            {
                return OperationResult.Fail("没有找到可执行文件；请改用 Steam 启动或在设置中重新选择游戏目录。");
            }

            try
            {
                _logger?.Info($"Launching directly: {installation.ExecutablePath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = installation.ExecutablePath,
                    WorkingDirectory = installation.GameDirectory,
                    UseShellExecute = true
                });
                return OperationResult.Ok($"已发送启动命令：{installation.Profile.EditionLabel}。");
            }
            catch (Exception exception)
            {
                return OperationResult.Fail($"启动失败：{exception.Message}");
            }
        }, cancellationToken);
    }

    public Task<OperationResult> OpenGameFolderAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(installation.GameDirectory))
                    return OperationResult.Fail("游戏目录不存在。");

                Process.Start(new ProcessStartInfo
                {
                    FileName = installation.GameDirectory,
                    UseShellExecute = true
                });
                return OperationResult.Ok("已打开游戏目录。");
            }
            catch (Exception exception)
            {
                return OperationResult.Fail($"打开目录失败：{exception.Message}");
            }
        }, cancellationToken);
    }
}
