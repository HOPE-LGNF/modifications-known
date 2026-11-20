using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Network;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Installation;
using ModificationsKnown.Infrastructure.Storage;
using ModificationsKnown.Infrastructure.Windows;

namespace ModificationsKnown.App.ViewModels;

public partial class InstalledViewModel
{
    private void OnModsChanged(IReadOnlyCollection<string> packageNames)
    {
        if (packageNames.Count == 0) return;

        foreach (var packageName in packageNames)
        {
            _updateCache.Remove(packageName);
            _latestFiles.Remove(packageName);
            foreach (var item in _allItems.Where(candidate =>
                         string.Equals(candidate.Mod.PackageId, packageName, StringComparison.OrdinalIgnoreCase)))
            {
                item.HasUpdate = false;
                item.LatestVersionLabel = string.Empty;
            }
        }

        HasUpdates = _allItems.Any(candidate => candidate.HasUpdate);
        _ = RefreshAfterModsChangedAsync();
    }

    private async Task RefreshAfterModsChangedAsync()
    {
        await RefreshCoreAsync();
        HasUpdates = _allItems.Any(candidate => candidate.HasUpdate);
        CanOneClickUpdate = CanOneClickUpdate && HasUpdates;
        UpdateSummary = HasUpdates ? "仍有可更新项，可重新检查。" : string.Empty;
    }

    private async Task EnsureNxmReadyAsync()
    {
        if (_nxmRegistrar.GetStatus().RegisteredForCurrentExecutable) return;

        var premium = false;
        try
        {
            var account = await _nexus.GetAccountStatusAsync();
            premium = account.IsPremium;
        }
        catch
        {
            // Unknown tier: let the user choose.
        }

        if (premium) return;

        var go = await _dialogs.PromptNxmAsync();
        if (!go) return;

        await NxmRegistrationFlow.RegisterAsync(_dialogs, _nxmRegistrar, _shell);
    }

    [RelayCommand]
    private async Task UpdateAsync(InstalledModItemViewModel item)
    {
        if (IsBusy || !item.IsNexusMatched) return;
        if (_shell.Game.Selected is not { } installation) return;
        if (!EnsureGameNotRunning(installation, "更新模组")) return;
        await EnsureNxmReadyAsync();

        IsBusy = true;
        try
        {
            var result = await UpdateItemCoreAsync(installation, item, askConfirmation: true, backup: true);
            if (result)
            {
                _updateCache.Remove(item.Mod.PackageId);
                item.HasUpdate = false;
                await RefreshCoreAsync();
                HasUpdates = _allItems.Any(candidate => candidate.HasUpdate);
                CanOneClickUpdate = CanOneClickUpdate && HasUpdates;
                UpdateSummary = HasUpdates ? "仍有可更新项，可重新检查。" : string.Empty;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (IsBusy) return;
        if (!CanOneClickUpdate)
        {
            _shell.SetStatus("一键更新需要 Nexus Premium 直链；免费账号请逐项更新。", StatusKind.Info);
            return;
        }

        if (_shell.Game.Selected is not { } installation) return;
        if (!EnsureGameNotRunning(installation, "更新模组")) return;
        await EnsureNxmReadyAsync();

        var targets = _allItems.Where(item => item.HasUpdate && item.IsNexusMatched).ToList();
        if (targets.Count == 0)
        {
            _shell.SetStatus("没有可更新的模组，请先检查更新。", StatusKind.Info);
            return;
        }

        var choice = await _dialogs.ConfirmUpdateAsync(
            "一键更新",
            $"将依次更新 {targets.Count} 个模组：\n\n{string.Join(Environment.NewLine, targets.Select(item => "· " + item.Name + " → " + item.UpdateBadgeLabel))}\n\n免费账号若无法获取直链，会在浏览器中打开对应页面并跳过；其余模组继续。",
            showBackupOption: true);
        if (choice is null) return;

        IsBusy = true;
        var succeeded = 0;
        var skipped = 0;
        try
        {
            foreach (var item in targets)
            {
                if (await UpdateItemCoreAsync(installation, item, askConfirmation: false, choice.Backup))
                {
                    succeeded++;
                    _updateCache.Remove(item.Mod.PackageId);
                    _latestFiles.Remove(item.Mod.PackageId);
                    item.HasUpdate = false;
                }
                else
                {
                    skipped++;
                }
            }

            await RefreshCoreAsync();
            HasUpdates = _allItems.Any(candidate => candidate.HasUpdate);
            CanOneClickUpdate = CanOneClickUpdate && HasUpdates;
            UpdateSummary = HasUpdates ? "仍有可更新项，可重新检查。" : string.Empty;
            _shell.SetStatus(
                $"一键更新完成：成功 {succeeded} 个" + (skipped > 0 ? $"，跳过/失败 {skipped} 个。" : "。"),
                skipped == 0 ? StatusKind.Success : StatusKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> UpdateItemCoreAsync(
        GameInstallation installation,
        InstalledModItemViewModel item,
        bool askConfirmation,
        bool backup)
    {
        var match = item.NexusMatch;
        if (match is null) return false;

        var domain = _settings.Current.NexusGameDomain;
        LatestFile? latestForPage = null;
        _logger.Info($"[Update] start package={item.Mod.PackageId} installed={item.Mod.UpdateVersion ?? "unknown"} match={match.GameScopedId}/{match.ModUid}");
        try
        {
            if (!_latestFiles.TryGetValue(item.Mod.PackageId, out var latest))
            {
                latest = await _updateResolver.ResolveLatestAsync(match);
                if (latest is null)
                {
                    _shell.SetStatus($"「{item.Name}」没有可下载的 Nexus 文件。", StatusKind.Warning);
                    return false;
                }

                _latestFiles[item.Mod.PackageId] = latest;
            }

            latestForPage = latest;
            var file = latest.File;
            var version = latest.Version;
            var effectiveVersion = latest.EffectiveVersion;
            _logger.Info($"[Update] candidate file={file.Id} version={effectiveVersion} fileVersionId={version.GameScopedId}");
            if (!VersionText.IsNewer(effectiveVersion, item.Mod.UpdateVersion))
            {
                ClearUpdate(item);
                UpdateSummary = "已是最新版本。";
                _shell.SetStatus($"「{item.Name}」已是最新版本。", StatusKind.Info);
                return false;
            }

            var links = await _nexus.GetDownloadLinksAsync(domain, version.GameScopedId);
            _logger.Info($"[Update] download links for fileVersion={version.GameScopedId}: {links.Count}");
            var link = links.FirstOrDefault(candidate =>
                Uri.TryCreate(candidate.Url, UriKind.Absolute, out _));
            if (link is null)
            {
                _shell.SetStatus(
                    $"Nexus 没有为「{item.Name}」返回直链；免费账号请到模组发现用 NXM 或浏览器下载。",
                    StatusKind.Warning);
                await OpenNexusFilePageAsync(match, domain, latest);
                return false;
            }

            if (askConfirmation)
            {
                var choice = await _dialogs.ConfirmUpdateAsync(
                    "更新模组",
                    $"「{item.Name}」已安装 {item.Mod.VersionLabel}，将更新到 {VersionText.Label(effectiveVersion)}。\n\n下载后替换旧包；更新失败会尝试回滚。",
                    showBackupOption: true);
                if (choice is null) return false;
                backup = choice.Backup;
            }

            Directory.CreateDirectory(AppPaths.DownloadsDirectory);
            var extension = Path.GetExtension(new Uri(link.Url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8) extension = ".zip";
            var fileName = PathSafety.GetSafeFileName(
                $"{item.Name}-{effectiveVersion}-{DateTime.Now:HHmmss}{extension}");
            var destination = Path.Combine(AppPaths.DownloadsDirectory, fileName);

            _shell.ReportProgress($"正在下载「{item.Name}」{VersionText.Label(effectiveVersion)}…");
            _logger.Info($"[Update] downloading {link.Url} -> {destination}");
            var download = await _downloader.DownloadAsync(link.Url, destination);
            _logger.Info($"[Update] download success={download.Success} message={download.Message}");
            if (!download.Success)
            {
                _shell.SetStatus(download.Message, StatusKind.Error);
                return false;
            }

            var source = PackageSource.Nexus(
                item.Name,
                domain,
                match.GameScopedId,
                match.ModUid,
                file.Id,
                effectiveVersion);
            var result = await _updates.InstallReplacingAsync(
                installation,
                item.Mod,
                destination,
                source,
                backup);
            _logger.Info($"[Update] install success={result.Success} backup={result.BackupPath ?? "none"} message={result.Message}");
            _shell.SetStatus(
                result.Message,
                result.Success
                    ? result.Warning ? StatusKind.Warning : StatusKind.Success
                    : StatusKind.Error);
            return result.Success;
        }
        catch (NexusApiException exception)
        {
            _logger.Warn($"[Update] Nexus API failed status={exception.StatusCode} message={exception.Message}");
            _shell.SetStatus(exception.Message, StatusKind.Error);
            if (exception.StatusCode is 401 or 403)
                await OpenNexusFilePageAsync(match, domain, latestForPage);
            return false;
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"更新「{item.Name}」失败：{exception.Message}", StatusKind.Error);
            return false;
        }
    }

    private async Task OpenNexusFilePageAsync(
        NexusMatch match,
        string domain,
        LatestFile? latest)
    {
        var url = $"https://www.nexusmods.com/{domain}/mods/{match.GameScopedId}?tab=files";
        long? legacyFileId = null;
        try
        {
            if (latest is not null)
            {
                legacyFileId = await _nexus.GetLegacyFileIdAsync(
                    domain,
                    match.GameScopedId,
                    latest.Version.Name,
                    latest.EffectiveVersion,
                    latest.Version.UploadedAt);

                if (legacyFileId is null)
                {
                    legacyFileId = await _nexus.GetLegacyFileIdAsync(
                        domain,
                        match.GameScopedId,
                        latest.File.Name,
                        latest.EffectiveVersion,
                        latest.File.LastUploadedAt);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Warn($"[Update] legacy file id lookup failed: {exception.Message}");
        }

        if (legacyFileId is not null)
            url += $"&file_id={legacyFileId.Value}";
        else
            _logger.Warn("[Update] no legacy file id; opening file page without file_id");

        url += "&nmm=1";
        _logger.Info($"[Update] opening {url}");

        try
        {
            var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _logger.Info($"[Update] Process.Start returned pid={process?.Id.ToString() ?? "null"}");
        }
        catch (Exception exception)
        {
            _logger.Error("[Update] failed to open Nexus file page", exception);
            _shell.SetStatus($"打开浏览器失败：{exception.Message}", StatusKind.Error);
        }
    }

    private void ClearUpdate(InstalledModItemViewModel item)
    {
        item.HasUpdate = false;
        item.LatestVersionLabel = string.Empty;
        _updateCache.Remove(item.Mod.PackageId);
        _latestFiles.Remove(item.Mod.PackageId);
        HasUpdates = _allItems.Any(candidate => candidate.HasUpdate);
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsBusy || IsCheckingUpdates) return;

        IsCheckingUpdates = true;
        UpdateSummary = "正在检查更新…";
        var updates = 0;
        var checkedCount = 0;
        var failed = 0;

        try
        {
            var installation = _shell.Game.Selected;
            foreach (var item in _allItems.ToList())
            {
                if (item.NexusMatch is null || string.IsNullOrWhiteSpace(item.NexusMatch.GameScopedId))
                    continue;

                // Loose plugins-root DLLs are separate scanner rows but cannot
                // be safely replaced as a package; don't advertise an update
                // that PackageUpdateService will always refuse.
                if (installation is not null
                    && string.Equals(
                        Path.GetFullPath(item.Mod.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(installation.PluginsDirectory).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    ClearUpdate(item);
                    continue;
                }

                checkedCount++;
                try
                {
                    var latest = await _updateResolver.ResolveLatestAsync(item.NexusMatch);
                    if (latest is not null
                        && VersionText.IsNewer(latest.EffectiveVersion, item.Mod.UpdateVersion))
                    {
                        item.HasUpdate = true;
                        item.LatestVersionLabel = VersionText.Label(latest.EffectiveVersion);
                        _updateCache[item.Mod.PackageId] = (true, item.LatestVersionLabel);
                        _latestFiles[item.Mod.PackageId] = latest;
                        updates++;
                    }
                    else
                    {
                        ClearUpdate(item);
                    }
                }
                catch
                {
                    failed++;
                }
            }

            var premium = false;
            try
            {
                var account = await _nexus.GetAccountStatusAsync();
                premium = account.IsPremium;
            }
            catch
            {
                // Unknown tier: keep one-click hidden.
            }

            HasUpdates = updates > 0;
            CanOneClickUpdate = premium && HasUpdates;
            var checkStatus = UpdateStatus.ForCheck(checkedCount, updates, failed);
            UpdateSummary = checkStatus.Message;
            _shell.SetStatus(checkStatus.Message, checkStatus.Kind);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }
}
