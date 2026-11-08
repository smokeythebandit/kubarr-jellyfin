using Jellyfin.Plugin.Kubarr.Identity;
using Jellyfin.Plugin.Kubarr.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Kubarr;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<KubarrSettings>();
        serviceCollection.AddSingleton<KubarrClient>();
        serviceCollection.AddSingleton<UserLinker>();
        serviceCollection.AddSingleton<KubarrAuthenticationProvider>();
        // Jellyfin 12 resolves sign-in providers from the container.
        serviceCollection.AddSingleton<IAuthenticationProvider>(sp => sp.GetRequiredService<KubarrAuthenticationProvider>());
        serviceCollection.AddHostedService<BootstrapService>();
        serviceCollection.AddHostedService<SyncPollService>();
    }
}
