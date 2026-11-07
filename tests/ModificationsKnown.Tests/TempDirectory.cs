namespace ModificationsKnown.Tests;

public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mk-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(params string[] parts)
    {
        var fullPath = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public string CreateFile(string relativePath, string content = "test")
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath);
        var parent = System.IO.Path.GetDirectoryName(fullPath);
        if (parent is not null) Directory.CreateDirectory(parent);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
