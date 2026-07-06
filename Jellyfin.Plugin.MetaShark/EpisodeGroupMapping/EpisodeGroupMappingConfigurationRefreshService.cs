// <copyright file="EpisodeGroupMappingConfigurationRefreshService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using MediaBrowser.Model.Plugins;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    public sealed class EpisodeGroupMappingConfigurationRefreshService : IHostedService
    {
        private static readonly Action<ILogger, int, Exception?> LogQueuedRefresh =
            LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, nameof(OnConfigurationChanged)), "[MetaShark] 剧集组映射配置变更已排队刷新. Count={Count}.");

        private static readonly Action<ILogger, Exception?> LogRefreshFailed =
            LoggerMessage.Define(LogLevel.Warning, new EventId(2, nameof(OnConfigurationChanged)), "[MetaShark] 剧集组映射配置变更刷新排队失败.");

        private readonly IEpisodeGroupMappingFacade episodeGroupMappingFacade;
        private readonly EpisodeGroupRefreshCoordinator episodeGroupRefreshCoordinator;
        private readonly ILogger<EpisodeGroupMappingConfigurationRefreshService> logger;
        private string lastEffectiveMapping = string.Empty;

        public EpisodeGroupMappingConfigurationRefreshService(
            IEpisodeGroupMappingFacade episodeGroupMappingFacade,
            EpisodeGroupRefreshCoordinator episodeGroupRefreshCoordinator,
            ILogger<EpisodeGroupMappingConfigurationRefreshService> logger)
        {
            this.episodeGroupMappingFacade = episodeGroupMappingFacade ?? throw new ArgumentNullException(nameof(episodeGroupMappingFacade));
            this.episodeGroupRefreshCoordinator = episodeGroupRefreshCoordinator ?? throw new ArgumentNullException(nameof(episodeGroupRefreshCoordinator));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plugin = MetaSharkPlugin.Instance;
            this.lastEffectiveMapping = this.episodeGroupMappingFacade.GetEffectiveMappingText(plugin?.Configuration);
            if (plugin != null)
            {
                plugin.ConfigurationChanged += this.OnConfigurationChanged;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plugin = MetaSharkPlugin.Instance;
            if (plugin != null)
            {
                plugin.ConfigurationChanged -= this.OnConfigurationChanged;
            }

            return Task.CompletedTask;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Configuration change event handlers must not throw into Jellyfin configuration saving; failures are logged and the saved mapping remains intact.")]
        private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
        {
            var newEffectiveMapping = this.episodeGroupMappingFacade.GetEffectiveMappingText(configuration as PluginConfiguration);
            var oldEffectiveMapping = this.lastEffectiveMapping;

            try
            {
                var outcome = this.episodeGroupRefreshCoordinator.QueueAffectedSeriesRefresh(oldEffectiveMapping, newEffectiveMapping);
                this.lastEffectiveMapping = newEffectiveMapping;
                if (outcome.RefreshResult.AffectedSeriesIds.Count > 0)
                {
                    LogQueuedRefresh(this.logger, outcome.QueuedCount, null);
                }
            }
            catch (Exception ex)
            {
                LogRefreshFailed(this.logger, ex);
            }
        }
    }
}
