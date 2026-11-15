using ModificationsKnown.App.ViewModels;
using ModificationsKnown.Infrastructure.Windows;

namespace ModificationsKnown.App.Services;

/// <summary>Shared “register NXM” flow used by Settings, Discover and update
/// prompts so the overwrite/backup questions stay identical everywhere.</summary>
public static class NxmRegistrationFlow
{
    public static async Task<bool> RegisterAsync(
        IUserDialogService dialogs,
        NxmProtocolRegistrar registrar,
        IAppShell shell)
    {
        var status = registrar.GetStatus();
        var hasOtherHandler = !status.RegisteredForCurrentExecutable
                              && !string.IsNullOrWhiteSpace(status.CurrentCommand);

        if (hasOtherHandler)
        {
            var confirmed = await dialogs.ConfirmAsync(
                "覆盖 NXM 关联",
                $"当前 nxm:// 由以下程序处理：\n{status.CurrentCommand}\n\n是否让 MK 覆盖它？其他程序的关联会先备份，取消注册时尝试恢复。",
                "覆盖",
                "取消");
            if (!confirmed) return false;
        }

        var result = registrar.Register(overwriteExisting: hasOtherHandler);
        shell.SetStatus(result.Message, result.Success ? StatusKind.Success : StatusKind.Error);
        return result.Success;
    }
}
