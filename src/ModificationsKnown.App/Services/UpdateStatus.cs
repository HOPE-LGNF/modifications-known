using ModificationsKnown.Core.Mods;

namespace ModificationsKnown.App.Services;

/// <summary>One place that maps update/install outcomes to a status message and
/// <see cref="StatusKind"/>. Callers should not derive "success" from counts
/// such as updates &gt; 0 or from ModInstallResult.Success (any package).</summary>
public static class UpdateStatus
{
    public static (string Message, StatusKind Kind) ForCheck(int checkedCount, int updates, int failed)
    {
        if (checkedCount == 0)
            return ("没有可检查的 Nexus 模组；先关联模组或配置 API Key。", StatusKind.Info);

        if (failed == checkedCount)
            return ($"检查更新失败（{failed} 个）", StatusKind.Error);

        if (updates > 0)
            return ($"发现 {updates} 个可更新" + (failed > 0 ? $"，{failed} 个检查失败" : ""), StatusKind.Warning);

        return (failed > 0
            ? $"已是最新版本，{failed} 个检查失败"
            : "已是最新版本", failed > 0 ? StatusKind.Warning : StatusKind.Success);
    }

    public static StatusKind ForInstall(ModInstallResult result) =>
        result.AllSucceeded ? StatusKind.Success
        : result.Success ? StatusKind.Warning
        : StatusKind.Error;

    public static (string Message, StatusKind Kind) ForUpdate(int succeeded, int skipped, int failed)
    {
        if (succeeded > 0)
            return ($"一键更新完成：成功 {succeeded} 个" +
                    (skipped + failed > 0 ? $"，跳过/失败 {skipped + failed} 个。" : "。"),
                skipped + failed > 0 ? StatusKind.Warning : StatusKind.Success);

        return failed > 0
            ? ($"一键更新失败：{failed} 个错误" + (skipped > 0 ? $"，{skipped} 个跳过。" : "。"), StatusKind.Error)
            : ("一键更新：没有可下载的更新。", StatusKind.Info);
    }
}
