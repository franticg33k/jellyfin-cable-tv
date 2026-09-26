using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.LiveTv;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CableTv;

/// <summary>
/// Registers the plugin's services with the server.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ContentPoolResolver>();
        serviceCollection.AddSingleton<ChannelStore>();
        serviceCollection.AddSingleton<GuideRefresher>();
        serviceCollection.AddSingleton<ILiveTvService, CableTvLiveTvService>();
        serviceCollection.AddHostedService<ConfigurationWatcher>();
    }
}
