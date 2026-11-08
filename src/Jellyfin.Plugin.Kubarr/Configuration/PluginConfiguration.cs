using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Kubarr.Configuration;

/// <summary>
/// State the plugin keeps between restarts. Everything Kubarr dictates comes
/// from the environment and mounted files instead (see <see cref="KubarrSettings"/>),
/// so nothing here needs editing by hand.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Kubarr user id to Jellyfin user id, for every account Kubarr owns.</summary>
    public List<UserLink> Links { get; set; } = new();

    /// <summary>Libraries the plugin created, so it never claims one a person made.</summary>
    public List<string> ManagedLibraries { get; set; } = new();

    /// <summary>GPU vendor whose encoding settings were last applied, or empty.</summary>
    public string AppliedGpuVendor { get; set; } = string.Empty;

    /// <summary>Whether Kubarr completed the startup wizard on this server.</summary>
    public bool WizardCompletedByKubarr { get; set; }
}

public class UserLink
{
    public string KubarrUserId { get; set; } = string.Empty;

    public Guid JellyfinUserId { get; set; }
}
