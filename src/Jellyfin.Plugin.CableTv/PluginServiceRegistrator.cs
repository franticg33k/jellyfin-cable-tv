using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.LiveTv;
using Jellyfin.Plugin.CableTv.Logos;
using Jellyfin.Plugin.CableTv.Packs;
using Jellyfin.Plugin.CableTv.Streaming;
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
        serviceCollection.AddSingleton<LogoService>();
        serviceCollection.AddSingleton<Weather.WeatherService>();
        serviceCollection.AddSingleton<ChannelSuggester>();
        serviceCollection.AddSingleton<PackService>();
        serviceCollection.AddSingleton<ChannelStore>();
        serviceCollection.AddSingleton<GuideRefresher>();
        serviceCollection.AddSingleton<StreamManager>();
        serviceCollection.AddSingleton<CommercialCache>();
        serviceCollection.AddSingleton<ILiveTvService, CableTvLiveTvService>();
        serviceCollection.AddHostedService<ConfigurationWatcher>();
    }
}
