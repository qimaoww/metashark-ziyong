// <copyright file="ServiceRegistrator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark
{
    using System;
    using System.IO;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Workers;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Plugins;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;

    /// <inheritdoc />
    public class ServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            ArgumentNullException.ThrowIfNull(serviceCollection);
            ArgumentNullException.ThrowIfNull(applicationHost);

            var dataFolderPath = MetaSharkPlugin.Instance?.DataFolderPath;
            if (string.IsNullOrWhiteSpace(dataFolderPath))
            {
                dataFolderPath = Path.Combine(Path.GetTempPath(), MetaSharkPlugin.PluginName);
            }

            serviceCollection.AddHostedService<BoxSetManager>();
            serviceCollection.AddHostedService<TvMissingImageRefillItemUpdatedWorker>();
            serviceCollection.AddHostedService<EpisodeTitleBackfillItemUpdatedWorker>();
            serviceCollection.AddHostedService<EpisodeTitleBackfillDeferredRetryWorker>();
            serviceCollection.AddHostedService<MovieSeriesPeopleRefreshStateItemUpdatedWorker>();
            serviceCollection.AddHostedService<EpisodeOverviewCleanupItemUpdatedWorker>();
            serviceCollection.AddHostedService<EpisodeOverviewCleanupDeferredRetryWorker>();
            serviceCollection.AddSingleton<ITvImageRefillStateStore>((ctx) =>
            {
                return new FileTvImageRefillStateStore(
                    Path.Combine(dataFolderPath, "tv-image-refill-state.json"),
                    ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton<IPeopleRefreshStateStore>((ctx) =>
            {
                return new FilePeopleRefreshStateStore(
                    Path.Combine(dataFolderPath, "people-refresh-state.json"),
                    ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton<ITvImageRefillOutcomeReporter, TvImageRefillOutcomeReporter>();
            serviceCollection.AddSingleton<ITvMissingImageRefillService, TvMissingImageRefillService>();
            serviceCollection.AddSingleton<IMissingMetadataSearchService, MissingMetadataSearchService>();
            serviceCollection.AddSingleton<IMovieSeriesPeopleOverwriteRefreshCandidateStore>((_) => InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore.Shared);
            serviceCollection.AddSingleton<IEpisodeTitleBackfillCandidateStore, InMemoryEpisodeTitleBackfillCandidateStore>();
            serviceCollection.AddSingleton<IEpisodeTitleBackfillPendingResolver, EpisodeTitleBackfillPendingResolver>();
            serviceCollection.AddSingleton<IEpisodeTitleBackfillPersistence, JellyfinEpisodeTitleBackfillPersistence>();
            serviceCollection.AddSingleton<IEpisodeTitleBackfillPostProcessService, EpisodeTitleBackfillPostProcessService>();
            serviceCollection.AddSingleton<MovieSeriesPeopleRefreshStatePostProcessService>();
            serviceCollection.AddSingleton<IEpisodeOverviewCleanupCandidateStore, InMemoryEpisodeOverviewCleanupCandidateStore>();
            serviceCollection.AddSingleton<IEpisodeOverviewCleanupPendingResolver, EpisodeOverviewCleanupPendingResolver>();
            serviceCollection.AddSingleton<IEpisodeOverviewCleanupPersistence, JellyfinEpisodeOverviewCleanupPersistence>();
            serviceCollection.AddSingleton<IEpisodeOverviewCleanupPostProcessService, EpisodeOverviewCleanupPostProcessService>();
            serviceCollection.AddSingleton((ctx) =>
            {
                return new DoubanApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new TmdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new OmdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new ImdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new TvdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
        }
    }
}
