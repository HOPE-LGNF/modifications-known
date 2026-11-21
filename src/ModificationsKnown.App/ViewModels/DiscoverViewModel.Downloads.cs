using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Common;
using ModificationsKnown.Core.Diagnostics;
using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Mods;
using ModificationsKnown.Core.Network;
using ModificationsKnown.Core.Nexus;
using ModificationsKnown.Core.Settings;
using ModificationsKnown.Infrastructure.Installation;
using ModificationsKnown.Infrastructure.Storage;
using ModificationsKnown.Infrastructure.Windows;

namespace ModificationsKnown.App.ViewModels;

public partial class DiscoverViewModel
{
    private async Task EnsureNxmReadyAsync()
    {
        _openNexusWithNxm = NxmRegistered;
        if (NxmRegistered) return;

        var premium = false;
        try
        {
            var account = await _nexus.GetAccountStatusAsync();
            premium = account.IsPremium;
        }
        catch
        {
            // Unknown tier: still offer NXM, especially when no API key is set.
        }

        if (premium) return;

        var go = await _dialogs.PromptNxmAsync();
        if (!go)
        {
            // The user declined NXM: the fallback page must not be opened with
            // nmm=1, otherwise Nexus immediately starts the NXM flow anyway.
            _openNexusWithNxm = false;
            return;
        }

        await NxmRegistrationFlow.RegisterAsync(_dialogs, _nxmRegistrar, _shell);
        RefreshNxmStatus();
        _openNexusWithNxm = NxmRegistered;
    }

    private async Task<(bool Proceed, InstalledMod? Target, bool Backup)> PrepareUpdateAsync(
        GameInstallation installation,
        string nexusName,
        string gameScopedId,
        string? incomingVersion)
    {
        InstalledMod? target;
        try
        {
            target = await _updateResolver.FindTargetAsync(installation, nexusName, gameScopedId);
        }
        catch
        {
            return (true, null, false);
        }

        if (target is null) return (true, null, false);

        var incoming = incomingVersion;
        if (!string.IsNullOrWhiteSpace(incoming)
            && !VersionText.IsNewer(incoming, target.UpdateVersion))
        {
            var message = $"已安装「{target.Name}」{target.VersionLabel}，不低于要安装的 {VersionText.Label(incoming)}，已跳过。";
            _shell.SetStatus(message, StatusKind.Info);
            DownloadStatus = message;
            return (false, null, false);
        }

        if (ModUpdateResolver.IsPluginsRoot(installation, target))
        {
            _shell.SetStatus("散放在 plugins 根目录的 DLL 无法安全更新；请先手动处理。", StatusKind.Warning);
            DownloadStatus = "无法更新散放 DLL。";
            return (false, null, false);
        }

        var incomingLabel = string.IsNullOrWhiteSpace(incoming)
            ? "未知版本"
            : VersionText.Label(incoming);
        var choice = await _dialogs.ConfirmUpdateAsync(
            "更新模组",
            $"检测到已安装「{target.Name}」{target.VersionLabel}。\n\n将更新到 {incomingLabel}。旧版本会先移动到同盘暂存目录用于失败回滚；勾选备份时会再复制一份到 MK 数据目录。",
            showBackupOption: true);
        if (choice is null)
        {
            DownloadStatus = "已取消更新。";
            return (false, null, false);
        }

        return (true, target, choice.Backup);
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (SelectedCard is null || SelectedVersion is null)
        {
            _shell.SetStatus("请先选择一个文件版本。", StatusKind.Warning);
            return;
        }

        if (_shell.Game.Selected is not { } installation)
        {
            _shell.SetStatus("请先选择游戏安装。", StatusKind.Warning);
            return;
        }

        if (!installation.HasBepInEx)
        {
            _shell.SetStatus("当前游戏没有安装 BepInEx，无法安装模组。", StatusKind.Warning);
            return;
        }

        await EnsureNxmReadyAsync();

        // Get the current NXM status for log/fallback page.
        var nxmReady = NxmRegistered && _openNexusWithNxm;

        // Pin the operation inputs. The user may change the selection while a
        // long download is running; the installation must still belong to the
        // mod/file that was chosen when the command started.
        var card = SelectedCard;
        var version = SelectedVersion;
        var file = SelectedFile;
        var effectiveVersion = NexusVersionText.Resolve(version, file);

        IsDownloading = true;
        ShowFreeDownloadWhy = false;
        DownloadStatus = "正在获取下载链接…";
        DownloadProgress = 0;
        try
        {
            var links = await _nexus.GetDownloadLinksAsync(
                _settings.Current.NexusGameDomain,
                version.GameScopedId);

            var link = links.FirstOrDefault(candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out _));
            if (link is null)
            {
                _shell.SetStatus("Nexus 没有返回可用的下载链接。", StatusKind.Error);
                return;
            }

            DownloadStatus = "正在下载…";
            var extension = GetExtension(link.Url);
            var fileName = $"{PathSafety.GetSafeFileName(card.Summary.Name)}-{PathSafety.GetSafeFileName(effectiveVersion)}{extension}";
            var destination = Path.Combine(AppPaths.DownloadsDirectory, fileName);

            var progress = new Progress<double>(value => DownloadProgress = value);
            var download = await _downloader.DownloadAsync(link.Url, destination, progress);
            if (!download.Success)
            {
                _shell.SetStatus(download.Message, StatusKind.Error);
                return;
            }

            var (proceed, updateTarget, backup) = await PrepareUpdateAsync(
                installation,
                card.Summary.Name,
                card.Summary.GameScopedId,
                effectiveVersion);
            if (!proceed) return;

            DownloadStatus = updateTarget is null ? "正在安装…" : "正在更新…";
            var source = PackageSource.Nexus(
                card.Summary.Name,
                _settings.Current.NexusGameDomain,
                card.Summary.GameScopedId,
                card.Summary.Id,
                file?.Id,
                effectiveVersion);
            ModInstallResult installed;
            if (updateTarget is null)
            {
                installed = await _operations.InstallArchiveAsync(installation, destination, source, progress);
            }
            else
            {
                var update = await _updates.InstallReplacingAsync(
                    installation,
                    updateTarget,
                    destination,
                    source,
                    backup,
                    progress);
                installed = new ModInstallResult(
                    update.Success,
                    update.Message,
                    update.Success
                        ? [new InstalledPackageResult(updateTarget.PackageId, true, update.Message)]
                        : []);
            }

            if (installed.Success)
            {
                await RecordSourceAsync(
                    installation,
                    installed,
                    card.Summary.Name,
                    _settings.Current.NexusGameDomain,
                    card.Summary.GameScopedId,
                    card.Summary.Id,
                    effectiveVersion,
                    file?.Id);
                _shell.SetStatus(
                    installed.Message,
                    installed.AllSucceeded ? StatusKind.Success : StatusKind.Warning);
                DownloadStatus = updateTarget is null ? "安装完成。" : "更新完成。";
                _shell.NotifyModsChanged(
                    installed.Packages.Where(package => package.Success)
                        .Select(package => package.PackageName)
                        .ToList());
            }
            else
            {
                _shell.SetStatus(installed.Message, StatusKind.Error);
                DownloadStatus = "安装未完成。";
            }
        }
        catch (NexusApiException exception)
        {
            if (exception.StatusCode == 403)
            {
                _pendingFreeDownload = new PendingFreeDownload(
                    installation,
                    _settings.Current.NexusGameDomain,
                    long.TryParse(card.Summary.GameScopedId, out var mg) ? mg : 0,
                    long.TryParse(file?.Id, out var fi) ? fi : 0,
                    card.Name,
                    effectiveVersion,
                    card.Summary.Id,
                    file?.Id);
                DownloadStatus = nxmReady
                    ? "普通 API Key 无法获取直链。已打开 Nexus 文件页，请点击页面上的 “Slow download”，并允许浏览器打开 MK，MK 会接管 NXM 下载。"
                    : "普通 API Key 无法获取直链，且暂未使用 NXM。已打开普通 Nexus 文件页，请点击 Slow Download 下载压缩包，再拖入 MK 安装。";
                ShowFreeDownloadWhy = true;
                await OpenNexusFilePageAsync(card, version, file);
                _shell.SetStatus(DownloadStatus, StatusKind.Warning);
                return;
            }

            _shell.SetStatus(exception.Message, StatusKind.Error);
            DownloadStatus = "下载链接不可用；请使用“在 Nexus 打开”。";
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"下载或安装失败：{exception.Message}", StatusKind.Error);
            DownloadStatus = "下载或安装失败。";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void RefreshNxmStatus()
    {
        var status = _nxmRegistrar.GetStatus();
        NxmRegistered = status.RegisteredForCurrentExecutable;
        NxmRegistrationHint = !status.Supported
            ? "当前平台不支持 NXM 协议注册。"
            : status.RegisteredForCurrentExecutable
                ? "NXM 已注册，可以打开浏览器文件页并使用 Mod manager download。"
                : string.IsNullOrWhiteSpace(status.CurrentCommand)
                    ? "还未注册 NXM 链接。请到设置 → 浏览器集成（NXM）注册。"
                    : $"nxm:// 当前由其他程序处理：{status.CurrentCommand}";
    }

    [RelayCommand]
    private Task OpenNexusFilePageAsync() =>
        OpenNexusFilePageAsync(SelectedCard, SelectedVersion, SelectedFile);

    private async Task OpenNexusFilePageAsync(
        ModCardViewModel? card,
        NexusFileVersion? version,
        NexusModFile? file)
    {
        card ??= SelectedCard;
        version ??= SelectedVersion;
        file ??= SelectedFile;
        if (card is null) return;

        // NXM registration is not required just to *view* the file page. Keep
        // the page openable with the file/nmm parameters even if the protocol
        // is not registered yet; downloading still needs NXM or a manual click.
        if (!NxmRegistered || !_openNexusWithNxm)
        {
            _shell.SetStatus(
                "已打开普通 Nexus 文件页（未使用 NXM）；可手动完成 Slow download。",
                StatusKind.Warning);
        }

        var domain = _settings.Current.NexusGameDomain;
        var modId = card.Summary.GameScopedId;
        var url = $"https://www.nexusmods.com/{domain}/mods/{modId}?tab=files";
        _logger.Info($"[NexusPage] registered={NxmRegistered} mod={modId} card={card.Summary.Id} version={version?.Version ?? "null"} file={file?.Id ?? "null"}");

        // v3 file ids are not the legacy ids the website expects. Resolve the
        // v1 file id by version name first, then by the file name. A failed
        // lookup must not prevent opening the page with &nmm=1.
        long? legacyFileId = null;
        try
        {
            if (version is not null)
            {
                legacyFileId = await _nexus.GetLegacyFileIdAsync(
                    domain, modId, version.Name, NexusVersionText.Resolve(version, file), version.UploadedAt);
            }

            if (legacyFileId is null && file is not null)
            {
                legacyFileId = await _nexus.GetLegacyFileIdAsync(
                    domain,
                    modId,
                    file.Name,
                    version?.Version ?? card.Summary.Version ?? string.Empty,
                    file.LastUploadedAt);
            }
        }
        catch
        {
            // Fall through: open the page without file_id.
        }

        if (legacyFileId is not null)
            url += $"&file_id={legacyFileId.Value}";
        else
            _shell.SetStatus("未能解析这个文件的网站 file_id，已打开文件页；请手动选择文件。", StatusKind.Warning);

        if (NxmRegistered && _openNexusWithNxm)
            url += "&nmm=1";

        _logger.Info($"[NexusPage] nxm={NxmRegistered && _openNexusWithNxm} opening {url}");

        try
        {
            var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _logger.Info($"[NexusPage] Process.Start returned pid={process?.Id.ToString() ?? "null"}");
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"打开浏览器失败：{exception.Message}", StatusKind.Error);
        }
    }

    public async Task HandleNxmLinkAsync(string raw)
    {
        if (IsDownloading) return;

        if (!NxmLinkParser.TryParse(raw, out var nxm, out var parseError) || nxm is null)
        {
            _shell.SetStatus(parseError, StatusKind.Error);
            DownloadStatus = parseError;
            return;
        }

        if (!string.Equals(nxm.GameDomain, _settings.Current.NexusGameDomain, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"NXM 游戏域是「{nxm.GameDomain}」，当前设置为「{_settings.Current.NexusGameDomain}」。请检查设置。";
            _shell.SetStatus(message, StatusKind.Error);
            DownloadStatus = message;
            return;
        }

        if (nxm.IsExpired(DateTimeOffset.UtcNow))
        {
            const string message = "NXM 下载授权已过期。请回到 Nexus 文件页重新点击“Mod manager download”。";
            _shell.SetStatus(message, StatusKind.Error);
            DownloadStatus = message;
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Current.NexusApiKey))
        {
            const string message = "处理 NXM 链接仍需要 Nexus API Key，请先在设置中填写。";
            _shell.SetStatus(message, StatusKind.Warning);
            DownloadStatus = message;
            return;
        }

        if (nxm.UserId is { } nxmUserId)
        {
            try
            {
                var account = await _nexus.GetAccountStatusAsync();
                if (account.UserId is { } accountUserId && accountUserId != nxmUserId)
                {
                    const string message = "这个 NXM 链接属于另一个 Nexus 账号，已拒绝。请用当前账号重新获取链接。";
                    _shell.SetStatus(message, StatusKind.Error);
                    DownloadStatus = message;
                    return;
                }
            }
            catch (NexusApiException exception)
            {
                _shell.SetStatus(exception.Message, StatusKind.Error);
                DownloadStatus = exception.Message;
                return;
            }
        }

        var pending = _pendingFreeDownload;
        var matchesPending = pending is not null
            && pending.GameDomain.Equals(nxm.GameDomain, StringComparison.OrdinalIgnoreCase)
            && pending.ModId == nxm.ModId
            && pending.FileId == nxm.FileId;

        GameInstallation? installation = matchesPending ? pending!.Installation : _shell.Game.Selected;
        if (installation is null)
        {
            _shell.SetStatus("请先选择游戏安装。", StatusKind.Warning);
            return;
        }

        if (!installation.HasBepInEx)
        {
            _shell.SetStatus("当前游戏没有安装 BepInEx，无法安装模组。", StatusKind.Warning);
            return;
        }

        if (matchesPending) _pendingFreeDownload = null;

        // The free-download explanation belongs to the 403 state only; once an
        // NXM link is accepted (or a download starts) it must disappear.
        ShowFreeDownloadWhy = false;
        IsDownloading = true;
        HasNxmFallback = false;
        _nxmFallbackUrl = null;
        DownloadStatus = "正在通过 NXM 获取下载链接…";
        DownloadProgress = 0;
        try
        {
            var links = await _nexus.GetNxmDownloadLinksAsync(
                nxm.GameDomain,
                nxm.ModId,
                nxm.FileId,
                nxm.Key,
                nxm.Expires);

            var link = links.FirstOrDefault(candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out _));
            if (link is null)
            {
                _shell.SetStatus("Nexus 没有返回可用的 NXM 下载链接。", StatusKind.Error);
                return;
            }

            DownloadStatus = "正在下载 NXM 文件…";
            _nxmFallbackUrl = link.Url;
            HasNxmFallback = true;
            var extension = GetExtension(link.Url);
            var fileName = $"nxm-{nxm.ModId}-{nxm.FileId}{extension}";
            var destination = Path.Combine(AppPaths.DownloadsDirectory, fileName);

            var progress = new Progress<double>(value => DownloadProgress = value);
            var download = await _downloader.DownloadAsync(link.Url, destination, progress);
            if (!download.Success)
            {
                _shell.SetStatus(download.Message, StatusKind.Error);
                DownloadStatus = download.Message + " 可点“浏览器打开直链”重试。";
                return;
            }

            HasNxmFallback = false;
            _nxmFallbackUrl = null;

            DownloadStatus = "正在读取 Nexus 元数据…";
            var nexusName = matchesPending ? pending!.NexusName : $"Nexus 模组 #{nxm.ModId}";
            var nexusVersion = matchesPending ? pending!.Version : null;
            var modUid = matchesPending ? pending!.ModUid : string.Empty;
            if (!matchesPending)
            {
                try
                {
                    var details = await _nexus.GetModAsync(nxm.GameDomain, nxm.ModId.ToString());
                    nexusName = details.Summary.Name;
                    nexusVersion = details.Summary.Version;
                    modUid = details.Summary.Id;
                }
                catch (Exception exception)
                {
                    _shell.SetStatus($"读取 Nexus 名称失败：{exception.Message}", StatusKind.Warning);
                }
            }

            var (proceed, updateTarget, backup) = await PrepareUpdateAsync(
                installation,
                nexusName,
                nxm.ModId.ToString(),
                nexusVersion);
            if (!proceed) return;

            DownloadStatus = updateTarget is null ? "正在安装 NXM 文件…" : "正在更新 NXM 文件…";
            var source = PackageSource.Nexus(
                nexusName,
                nxm.GameDomain,
                nxm.ModId.ToString(),
                modUid,
                nxm.FileId.ToString(),
                nexusVersion);
            ModInstallResult installed;
            if (updateTarget is null)
            {
                installed = await _operations.InstallArchiveAsync(installation, destination, source, progress);
            }
            else
            {
                var update = await _updates.InstallReplacingAsync(
                    installation,
                    updateTarget,
                    destination,
                    source,
                    backup,
                    progress);
                installed = new ModInstallResult(
                    update.Success,
                    update.Message,
                    update.Success
                        ? [new InstalledPackageResult(updateTarget.PackageId, true, update.Message)]
                        : []);
            }

            if (installed.Success)
            {
                await RecordSourceAsync(
                    installation,
                    installed,
                    nexusName,
                    nxm.GameDomain,
                    nxm.ModId.ToString(),
                    modUid,
                    nexusVersion,
                    nxm.FileId.ToString());
            }

            if (installed.Success)
            {
                _shell.NotifyModsChanged(
                    installed.Packages.Where(package => package.Success)
                        .Select(package => package.PackageName)
                        .ToList());
            }

            _shell.SetStatus(
                installed.Message,
                installed.AllSucceeded
                    ? StatusKind.Success
                    : installed.Success
                        ? StatusKind.Warning
                        : StatusKind.Error);
            DownloadStatus = installed.AllSucceeded ? "安装完成。" : "安装未完成。";
        }
        catch (NexusApiException exception)
        {
            _shell.SetStatus(exception.Message, StatusKind.Error);
            DownloadStatus = exception.Message;
        }
        catch (Exception exception)
        {
            _shell.SetStatus($"NXM 下载失败：{exception.Message}", StatusKind.Error);
            DownloadStatus = "NXM 下载失败。";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private async Task RecordSourceAsync(
        GameInstallation installation,
        ModInstallResult result,
        string nexusName,
        string gameDomain,
        string gameScopedId,
        string modUid,
        string? version,
        string? fileId)
    {
        var sources = result.Packages
            .Where(package => package.Success)
            .Select(package => new InstalledSource(
                installation.GameDirectory,
                package.PackageName,
                nexusName,
                gameDomain,
                gameScopedId,
                modUid,
                version,
                fileId,
                DateTimeOffset.UtcNow))
            .ToList();

        await _sources.RecordAsync(sources);
    }
}
