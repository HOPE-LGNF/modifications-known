namespace ModificationsKnown.Core.Settings;

/// <summary>
/// User-facing launcher settings. Secrets are never logged. The Nexus API key
/// is stored locally because a desktop launcher cannot complete the web OAuth
/// flow on the user's behalf; the settings UI explains this.
/// </summary>
public sealed class AppSettings
{
    public string? GamePath { get; set; }

    public string NexusApiKey { get; set; } = string.Empty;

    public string NexusGameDomain { get; set; } = "scavprototype";

    public string ThemePreset { get; set; } = "moss";

    public string? PreferredInstallationId { get; set; }

    public bool ConfirmBeforeDelete { get; set; } = true;

    public bool IncludeAdultMods { get; set; }

    public int PageSize { get; set; } = 24;
}
