using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// Rebuilds channels and refreshes the guide whenever the plugin configuration is saved.
/// </summary>
public sealed class ConfigurationWatcher : IHostedService
{
    private readonly GuideRefresher _refresher;
    private readonly ILogger<ConfigurationWatcher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationWatcher"/> class.
    /// </summary>
    /// <param name="refresher">Guide refresher.</param>
    /// <param name="logger">Logger.</param>
    public ConfigurationWatcher(GuideRefresher refresher, ILogger<ConfigurationWatcher> logger)
    {
        _refresher = refresher;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        try
        {
            _refresher.RebuildAndRefreshGuide();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild Cable TV channels after a configuration change");
        }
    }
}
