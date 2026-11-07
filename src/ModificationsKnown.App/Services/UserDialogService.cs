using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ModificationsKnown.App.Views;

namespace ModificationsKnown.App.Services;

public interface IUserDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "确认", string cancelText = "取消");

    Task AlertAsync(string title, string message, string closeText = "知道了");
}

public sealed class UserDialogService : IUserDialogService
{
    private readonly Func<Window?> _ownerProvider;

    public UserDialogService(Func<Window?> ownerProvider)
    {
        _ownerProvider = ownerProvider;
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

    public async Task AlertAsync(string title, string message, string closeText = "知道了")
    {
        var owner = _ownerProvider();
        if (owner is null) return;

        var dialog = new ConfirmDialog(title, message, closeText, string.Empty, showCancel: false);
        await dialog.ShowDialog<bool>(owner);
    }
}

public interface IFilePickerService
{
    Task<string?> PickExecutableAsync();

    Task<string?> PickFolderAsync();

    Task<string?> PickArchiveAsync();

    Task<string?> PickThemeFileAsync();
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
