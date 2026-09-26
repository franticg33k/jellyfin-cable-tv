using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// Rebuilds channels and refreshes the guide whenever the plugin configuration is saved, and once shortly after the
/// server starts, so an upgraded plugin's schedule reaches the guide without waiting for Jellyfin's daily refresh.
/// </summary>
public sealed class ConfigurationWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private readonly CancellationTokenSource _stopping = new();
    private static readonly TimeSpan WarmUpDelay = TimeSpan.FromSeconds(10);

    private readonly GuideRefresher _refresher;
    private readonly ChannelStore _store;
    private readonly ILogger<ConfigurationWatcher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationWatcher"/> class.
    /// </summary>
    /// <param name="refresher">Guide refresher.</param>
    /// <param name="store">Channel store.</param>
    /// <param name="logger">Logger.</param>
    public ConfigurationWatcher(GuideRefresher refresher, ChannelStore store, ILogger<ConfigurationWatcher> logger)
    {
        _refresher = refresher;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            if (string.IsNullOrEmpty(plugin.Configuration.StreamKey))
            {
                // The server's own ffmpeg reads channel streams over loopback HTTP without a user token; this secret
                // is what it presents instead.
                plugin.Configuration.StreamKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                plugin.SaveConfiguration();
            }

            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        _ = WarmUpAsync(_stopping.Token);
        _ = RefreshAfterStartupAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the channels in the background soon after start-up, so the first client request doesn't wait for it.
    /// </summary>
    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(WarmUpDelay, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => _ = _store.Channels, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cable TV warm-up failed; channels will be built on first use");
        }
    }

    private async Task RefreshAfterStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);
            _refresher.RebuildAndRefreshGuide();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Cable TV channels after startup");
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        try
        {
            _refresher.RequestRebuild();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild Cable TV channels after a configuration change");
        }
    }
}
