using System.Text.Json.Serialization;
using ModificationsKnown.Core.Updates;

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

    /// <summary>Once true, the user's theme choice is never migrated again.</summary>
    public bool ThemePresetMigrated { get; set; }

    public string? PreferredInstallationId { get; set; }

    public string? PreferredGamePath { get; set; }

    public bool ConfirmBeforeDelete { get; set; } = true;

    /// <summary>When true and the selected instance is already running, the
    /// launch button offers to close the game instead of starting another copy.</summary>
    public bool PreventMultipleInstances { get; set; } = true;

    public bool IncludeAdultMods { get; set; }

    public int PageSize { get; set; } = 24;

    public DateTimeOffset? LastLaunchAt { get; set; }

    /// <summary>Which release feed the launcher's own update check follows.
    /// Stable by default; unknown or missing values fall back to Stable.</summary>
    [JsonConverter(typeof(AppUpdateChannelJsonConverter))]
    public AppUpdateChannel UpdateChannel { get; set; } = AppUpdateChannel.Stable;
}
