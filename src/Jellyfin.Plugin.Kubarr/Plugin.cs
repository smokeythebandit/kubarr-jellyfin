using Jellyfin.Plugin.Kubarr.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Kubarr;

/// <summary>
/// Kubarr: sign in to Jellyfin with Kubarr accounts, and set Jellyfin up
/// without its startup wizard.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    public static readonly Guid PluginId = Guid.Parse("6deccf4f-c5ca-4286-8d37-aec40a40f122");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Kubarr";

    public override Guid Id => PluginId;

    public override string Description =>
        "Kubarr accounts sign in to Jellyfin, and Kubarr configures the server.";
}
