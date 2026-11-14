namespace ModificationsKnown.App.ViewModels;

public sealed record NavigationItem(
    string Key,
    string Title,
    string Subtitle,
    string? Badge = null,
    bool IsEnabled = true)
{
    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);
}
