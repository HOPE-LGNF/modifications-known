using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ModificationsKnown.App.Views;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Instances;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.App.Services;

public sealed record UpdateModChoice(bool Backup);

public interface IUserDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "确认", string cancelText = "取消");

    Task<bool> ConfirmDangerAsync(string title, string message, string confirmText = "确认", string cancelText = "取消");

    Task AlertAsync(string title, string message, string closeText = "知道了");

    Task<string?> PromptAsync(
        string title,
        string message,
        string initialValue = "",
        string watermark = "名称");

    Task<NexusMatch?> PickNexusMatchAsync(string gameDomain, string suggestedName);

    Task<UpdateModChoice?> ConfirmUpdateAsync(string title, string message, bool showBackupOption = true);

    Task<bool> PromptNxmAsync();

    Task<bool> ConfirmBepInExUninstallAsync(GameInstallation installation);

    Task<InstanceDeletionPlan?> PickInstanceDeletionAsync(
        GameInstallation installation,
        IReadOnlyList<SaveGroup> groups);

    /// <summary>
    /// Asks how to handle real saves that MK has not taken over yet, and which
    /// registered installation should receive them. Returning null
    /// (cancel/close) means the caller must not touch any file.
    /// </summary>
    Task<UnmanagedSaveDecision?> PickUnmanagedSaveAsync(
        string liveDirectory,
        int fileCount,
        IReadOnlyList<UnmanagedSaveCandidate> candidates,
        string? selectedInstallationKey);
}

public sealed class UserDialogService : IUserDialogService
{
    private readonly Func<Window?> _ownerProvider;
    private readonly INexusModsClient _nexus;

    public UserDialogService(Func<Window?> ownerProvider, INexusModsClient nexus)
    {
        _ownerProvider = ownerProvider;
        _nexus = nexus;
    }

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "确认",
        string cancelText = "取消")
    {
        var owner = _ownerProvider();
        if (owner is null) return false;

        var dialog = new ConfirmDialog(title, message, confirmText, cancelText, showCancel: true);
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<bool> ConfirmBepInExUninstallAsync(GameInstallation installation)
    {
        var owner = _ownerProvider();
        if (owner is null) return false;

        var dialog = new UninstallBepInExDialog(installation);
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<bool> PromptNxmAsync()
    {
        var owner = _ownerProvider();
        if (owner is null) return false;

        var dialog = new NxmPromptDialog();
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<UpdateModChoice?> ConfirmUpdateAsync(
        string title,
        string message,
        bool showBackupOption = true)
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var dialog = new UpdateModDialog(title, message, showBackupOption);
        return await dialog.ShowDialog<UpdateModChoice?>(owner);
    }

    public async Task<bool> ConfirmDangerAsync(
        string title,
        string message,
        string confirmText = "确认",
        string cancelText = "取消")
    {
        var owner = _ownerProvider();
        if (owner is null) return false;

        var dialog = new ConfirmDialog(title, message, confirmText, cancelText, showCancel: true, danger: true);
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task AlertAsync(string title, string message, string closeText = "知道了")
    {
        var owner = _ownerProvider();
        if (owner is null) return;

        var dialog = new ConfirmDialog(title, message, closeText, string.Empty, showCancel: false);
        await dialog.ShowDialog<bool>(owner);
    }

    public async Task<string?> PromptAsync(
        string title,
        string message,
        string initialValue = "",
        string watermark = "名称")
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var dialog = new TextPromptDialog(title, message, initialValue, watermark);
        return await dialog.ShowDialog<string?>(owner);
    }

    public async Task<InstanceDeletionPlan?> PickInstanceDeletionAsync(
        GameInstallation installation,
        IReadOnlyList<SaveGroup> groups)
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var dialog = new DeleteInstanceDialog(installation, groups);
        return await dialog.ShowDialog<InstanceDeletionPlan?>(owner);
    }

    public async Task<NexusMatch?> PickNexusMatchAsync(string gameDomain, string suggestedName)
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var dialog = new LinkNexusDialog(_nexus, gameDomain, suggestedName);
        return await dialog.ShowDialog<NexusMatch?>(owner);
    }

    public async Task<UnmanagedSaveDecision?> PickUnmanagedSaveAsync(
        string liveDirectory,
        int fileCount,
        IReadOnlyList<UnmanagedSaveCandidate> candidates,
        string? selectedInstallationKey)
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var dialog = new UnmanagedSaveDialog(liveDirectory, fileCount, candidates, selectedInstallationKey);
        return await dialog.ShowDialog<UnmanagedSaveDecision?>(owner);
    }
}

public interface IFilePickerService
{
    Task<string?> PickExecutableAsync();

    Task<string?> PickFolderAsync();

    Task<string?> PickArchiveAsync();

    Task<string?> PickThemeFileAsync();

    Task<string?> PickDataFolderAsync();

    Task<string?> PickBackupFolderAsync();

    Task<string?> PickSaveArchiveTargetAsync(string suggestedFileName);

    Task<string?> PickSaveArchiveAsync();
}

public sealed class FilePickerService : IFilePickerService
{
    private readonly Func<Window?> _ownerProvider;

    public FilePickerService(Func<Window?> ownerProvider)
    {
        _ownerProvider = ownerProvider;
    }

    public async Task<string?> PickExecutableAsync()
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择《未知伤亡》可执行文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Windows 可执行文件") { Patterns = ["*.exe"] },
                new FilePickerFileType("所有文件") { Patterns = ["*"] }
            ]
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    public async Task<string?> PickFolderAsync()
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择《未知伤亡》游戏目录",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    public async Task<string?> PickArchiveAsync()
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择已下载的模组压缩包",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("模组压缩包") { Patterns = ["*.zip", "*.7z", "*.rar"] },
                new FilePickerFileType("所有文件") { Patterns = ["*"] }
            ]
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    public async Task<string?> PickDataFolderAsync()
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 MK 数据目录（设置、日志与缓存）",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    public async Task<string?> PickBackupFolderAsync()
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择存档备份目录",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    public async Task<string?> PickSaveArchiveTargetAsync(string suggestedFileName)
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出存档组",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("存档组 ZIP") { Patterns = ["*.zip"] }
            ]
        });

        return file?.Path.LocalPath;
    }

    public async Task<string?> PickSaveArchiveAsync()
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择存档组 ZIP",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("存档组 ZIP") { Patterns = ["*.zip"] }
            ]
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    public async Task<string?> PickThemeFileAsync()
    {
        var owner = _ownerProvider();
        if (owner is null) return null;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入启动器主题（JSON）",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("主题 JSON") { Patterns = ["*.json"] },
                new FilePickerFileType("所有文件") { Patterns = ["*"] }
            ]
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }
}
