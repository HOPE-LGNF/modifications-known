using System.Text.Json;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App.Services;

public sealed record ActivityEntry(
    DateTimeOffset Timestamp,
    string Kind,
    string Title,
    string Detail)
{
    public string RelativeTime
    {
        get
        {
            var delta = DateTimeOffset.Now - Timestamp;
            if (delta < TimeSpan.FromMinutes(1)) return "刚刚";
            if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes} 分钟前";
            if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours} 小时前";
            if (delta < TimeSpan.FromDays(2)) return "昨天";
            return Timestamp.ToLocalTime().ToString("MM-dd HH:mm");
        }
    }

    public string Glyph => Kind switch
    {
        "launch" => "▶",
        "install" => "↓",
        "version" => "✎",
        "settings" => "⚙",
        "warning" => "!",
        "error" => "×",
        _ => "✓"
    };
}

/// <summary>Small persisted activity trail used by the overview page. Bounded
/// to the newest 60 entries and de-duplicated within a short window.</summary>
public sealed class ActivityLogService
{
    private const int Capacity = 60;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private List<ActivityEntry>? _cache;

    public ActivityLogService()
    {
        _path = Path.Combine(AppPaths.RootDirectory, "activity.json");
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ActivityEntry> Recent(int count)
    {
        lock (_gate)
        {
            return Load().TakeLast(Math.Max(1, count)).Reverse().ToList();
        }
    }

    public void Add(string kind, string title, string detail = "")
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        lock (_gate)
        {
            var list = Load();
            var now = DateTimeOffset.Now;
            if (list.LastOrDefault() is { } last
                && now - last.Timestamp < TimeSpan.FromSeconds(45)
                && string.Equals(last.Title, title, StringComparison.Ordinal))
            {
                return;
            }

            list.Add(new ActivityEntry(now, kind, title.Trim(), detail.Trim()));
            if (list.Count > Capacity) list.RemoveRange(0, list.Count - Capacity);
            Save(list);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<ActivityEntry> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(_path))
            {
                _cache = JsonSerializer.Deserialize<List<ActivityEntry>>(
                    File.ReadAllText(_path), JsonOptions) ?? [];
            }
        }
        catch
        {
            // A broken activity file must not break the launcher.
        }

        return _cache ??= [];
    }

    private void Save(List<ActivityEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(entries, JsonOptions));
            _cache = entries;
        }
        catch
        {
            // In-memory activity is enough if disk is read-only.
        }
    }
}
