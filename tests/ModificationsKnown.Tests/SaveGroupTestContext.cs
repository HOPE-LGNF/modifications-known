using ModificationsKnown.Core.Games;
using ModificationsKnown.Core.Saves;
using ModificationsKnown.Infrastructure.Saves;

namespace ModificationsKnown.Tests;

internal sealed class FakeSaveLinker : ISaveLinker
{
    public bool IsSupported { get; set; } = true;

    public Dictionary<string, string> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fail the next N create calls (used to simulate a failed switch
    /// while still allowing the rollback re-create to succeed).</summary>
    public int FailNextCreate { get; set; }

    public bool FailRemove { get; set; }

    public bool Exists(string path) => Directory.Exists(path);

    public bool TryGetTarget(string path, out string target)
    {
        if (Targets.TryGetValue(Normalize(path), out var value))
        {
            target = value;
            return true;
        }

        target = string.Empty;
        return false;
    }

    /// <summary>Records a link target without creating the directory, so a test
    /// can put files at the link path: the fake link is a plain directory, so
    /// those files would belong to the target in a real junction.</summary>
    public void SetTarget(string linkPath, string targetPath) =>
        Targets[Normalize(linkPath)] = Normalize(targetPath);

    public bool CreateJunction(string linkPath, string targetPath, out string error)
    {
        if (FailNextCreate > 0)
        {
            FailNextCreate--;
            error = "simulated create failure";
            return false;
        }

        Targets[Normalize(linkPath)] = Normalize(targetPath);
        Directory.CreateDirectory(linkPath);
        error = string.Empty;
        return true;
    }

    public bool RemoveJunction(string linkPath, out string error)
    {
        if (FailRemove)
        {
            error = "simulated remove failure";
            return false;
        }

        Targets.Remove(Normalize(linkPath));
        if (Directory.Exists(linkPath))
        {
            // Removing a real junction never touches the target's data; the
            // fake's link is a plain directory, so anything placed at the link
            // path in a test is link-side content and goes with it.
            Directory.Delete(linkPath, recursive: true);
        }

        error = string.Empty;
        return true;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

internal sealed class FakeRecycleBin : IRecycleBin
{
    public List<string> SentPaths { get; } = [];

    public bool Fail { get; set; }

    public bool DeleteAfterSend { get; set; } = true;

    public bool TrySendDirectory(string path, out string error)
    {
        if (Fail)
        {
            error = "simulated recycle failure";
            return false;
        }

        SentPaths.Add(Path.GetFullPath(path));
        if (DeleteAfterSend && Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        error = string.Empty;
        return true;
    }
}

internal sealed class SaveGroupTestContext : IDisposable
{
    public SaveGroupTestContext()
    {
        Temp = new TempDirectory();
        ProfilesRoot = Temp.CreateDirectory("profiles");
        BackupsRoot = Temp.CreateDirectory("backups");
        StagingRoot = Temp.CreateDirectory("staging");
        Live = Path.Combine(Temp.Path, "LocalLow", "Orsoniks", "CasualtiesUnknown");
        CatalogPath = Path.Combine(Temp.Path, "save-groups.json");
        GameDirectory = Temp.CreateDirectory("game");
        Installation = new GameInstallation(KnownGameProfiles.Full, GameDirectory, null, false, false);
        Service = BuildService();
    }

    public TempDirectory Temp { get; }

    public string ProfilesRoot { get; }

    public string BackupsRoot { get; }

    public string StagingRoot { get; }

    public string Live { get; }

    public string CatalogPath { get; }

    public string GameDirectory { get; }

    public GameInstallation Installation { get; }

    public FakeSaveLinker Linker { get; } = new();

    public FakeRecycleBin RecycleBin { get; } = new();

    public bool Running { get; set; }

    /// <summary>Forces the fresh probe result (e.g. <c>Unknown</c> when process
    /// information cannot be read). Null follows <see cref="Running"/>.</summary>
    public GameRunningState? ProbeState { get; set; }

    public SaveGroupService Service { get; }

    public string InstallationKey => SaveGroupFormat.GetInstallationKey(GameDirectory);

    public string InstallationDirectory => Path.Combine(ProfilesRoot, InstallationKey);

    public SaveGroupService BuildService(string? profilesRoot = null, string? catalogPath = null) =>
        new(new SaveGroupOptions
        {
            ProfilesRoot = profilesRoot ?? ProfilesRoot,
            CatalogPath = catalogPath ?? CatalogPath,
            BackupsRoot = BackupsRoot,
            StagingRoot = StagingRoot,
            LiveSaveDirectory = Live,
            Linker = Linker,
            RecycleBin = RecycleBin,
            IsGameRunning = _ => Running,
            ProbeGameRunningState = _ => ProbeState
                                         ?? (Running ? GameRunningState.Running : GameRunningState.NotRunning)
        });

    public string WriteLegacyFile(string relative, string content)
    {
        var path = Path.Combine(InstallationDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteLiveFile(string relative, string content)
    {
        var path = Path.Combine(Live, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void CreateLiveDirectory() => Directory.CreateDirectory(Live);

    public void Dispose() => Temp.Dispose();
}
