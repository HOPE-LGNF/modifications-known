using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.App.ViewModels;

public sealed record SaveFileRow(string RelativePath, string SizeLabel);

public partial class SaveInstanceItem : ObservableObject
{
    public SaveInstanceItem(
        GameInstallation installation,
        string profilePath,
        bool exists,
        IReadOnlyList<SaveFileRow> files,
        string sizeLabel,
        string updatedLabel,
        bool isLive,
        Func<SaveInstanceItem, Task> open,
        Func<SaveInstanceItem, Task> backup,
        Func<SaveInstanceItem, Task> delete)
    {
        Installation = installation;
        ProfilePath = profilePath;
        Exists = exists;
        Files = files;
        SizeLabel = sizeLabel;
        UpdatedLabel = updatedLabel;
        IsLive = isLive;
        OpenCommand = new AsyncRelayCommand(() => open(this));
        BackupCommand = new AsyncRelayCommand(() => backup(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }

    public GameInstallation Installation { get; }

    public string Name => Installation.DisplayLabel;

    public string GamePath => Installation.GameDirectory;

    public string ProfilePath { get; }

    public bool Exists { get; }

    public IReadOnlyList<SaveFileRow> Files { get; }

    public string SizeLabel { get; }

    public string UpdatedLabel { get; }

    public bool IsLive { get; }

    public bool IsEmpty => Files.Count == 0;

    public string FileCountLabel => $"{Files.Count} 个文件";

    public IAsyncRelayCommand OpenCommand { get; }

    public IAsyncRelayCommand BackupCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }
}

public partial class SavesViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly SaveIsolationService _saveIsolation;
    private readonly IUserDialogService _dialogs;
    private readonly IFilePickerService _filePicker;
    private readonly IAppLogger _logger;

    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    public SavesViewModel(
        IAppShell shell,
        SaveIsolationService saveIsolation,
        IUserDialogService dialogs,
        IFilePickerService filePicker,
        IAppLogger logger)
    {
        _shell = shell;
        _saveIsolation = saveIsolation;
        _dialogs = dialogs;
        _filePicker = filePicker;
        _logger = logger;
        _shell.Game.Changed += (_, _) => _ = RefreshAsync();
    }

    public ObservableCollection<SaveInstanceItem> Instances { get; } = [];

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync();

    private Task RefreshCoreAsync()
    {
        Instances.Clear();
        var live = OperatingSystem.IsWindows() ? _saveIsolation.GetLiveSaveDirectory() : null;
        string? liveTarget = null;
        try
        {
            if (live is not null && Directory.Exists(live))
            {
                var info = new DirectoryInfo(live);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    liveTarget = info.LinkTarget;
            }
        }
        catch
        {
            // A broken junction just means no item is marked as live.
        }

        foreach (var installation in _shell.Game.Installations)
        {
            var profile = _saveIsolation.GetProfileDirectory(installation);
            var files = new List<SaveFileRow>();
            var total = 0L;
            DateTime? updated = null;

            try
            {
                if (Directory.Exists(profile))
                {
                    foreach (var path in Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories))
                    {
                        if (string.Equals(Path.GetFileName(path), ".mk-save-profile", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var info = new FileInfo(path);
                        files.Add(new SaveFileRow(
                            Path.GetRelativePath(profile, path),
                            FormatSize(info.Length)));
                        total += info.Length;
                        if (updated is null || info.LastWriteTime > updated) updated = info.LastWriteTime;
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.Warn($"读取存档失败：{exception.Message}");
            }

            var isLive = liveTarget is not null && PathEquals(liveTarget, profile);
            Instances.Add(new SaveInstanceItem(
                installation,
                profile,
                Directory.Exists(profile),
                files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
                FormatSize(total),
                updated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—",
                isLive,
                OpenAsync,
                BackupAsync,
                DeleteAsync));
        }

        EmptyMessage = Instances.Count == 0
            ? "还没有添加游戏版本。先在设置中添加实例。"
            : string.Empty;
        return Task.CompletedTask;
    }

    private Task OpenAsync(SaveInstanceItem item)
    {
        try
        {
            if (!Directory.Exists(item.ProfilePath))
            {
                _shell.SetStatus("该版本的存档目录还不存在；首次从 MK 启动时会创建。", StatusKind.Info);
                return Task.CompletedTask;
            }

            Process.Start(new ProcessStartInfo(item.ProfilePath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开存档目录失败：{exception.Message}", StatusKind.Error);
        }

        return Task.CompletedTask;
    }

    private async Task BackupAsync(SaveInstanceItem item)
    {
        if (!item.Exists || item.Files.Count == 0)
        {
            _shell.SetStatus("该版本还没有可备份的存档。", StatusKind.Warning);
            return;
        }

        var folder = await _filePicker.PickBackupFolderAsync();
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            var target = Path.Combine(
                folder,
                $"{PathSafetySafe(item.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(target);

            foreach (var file in Directory.EnumerateFiles(item.ProfilePath, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), ".mk-save-profile", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = Path.GetRelativePath(item.ProfilePath, file);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: false);
            }

            _shell.SetStatus($"已备份「{item.Name}」的 {item.Files.Count} 个存档文件到 {target}。", StatusKind.Success);
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"备份存档失败：{exception.Message}", StatusKind.Error);
        }
    }

    private async Task DeleteAsync(SaveInstanceItem item)
    {
        if (item.IsLive)
        {
            await _dialogs.AlertAsync(
                "无法删除当前存档",
                $"「{item.Name}」的存档目录正被游戏链接使用。请先切换到其他版本，或从 MK 之外启动游戏前处理。");
            return;
        }

        if (!item.Exists)
        {
            _shell.SetStatus("该版本的存档目录不存在。", StatusKind.Info);
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "删除存档目录",
            $"将删除「{item.Name}」的托管存档：\n{item.ProfilePath}\n\n共 {item.FileCountLabel}，此操作不可恢复。",
            "删除存档",
            "取消");
        if (!confirmed) return;

        try
        {
            Directory.Delete(item.ProfilePath, recursive: true);
            _shell.SetStatus($"已删除「{item.Name}」的存档目录。", StatusKind.Success);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"删除存档失败：{exception.Message}", StatusKind.Error);
        }
    }

    private static string PathSafetySafe(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name.Trim();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
