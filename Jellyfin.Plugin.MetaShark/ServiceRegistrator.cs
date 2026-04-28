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
    using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Plugins;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.IO;
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
            serviceCollection.AddHostedService<SeriesTmdbProviderIdMigrationWorker>();
            serviceCollection.AddHostedService<TvMissingImageRefillItemUpdatedWorker>();
            serviceCollection.AddHostedService<PersonMissingImageRefillItemUpdatedWorker>();
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
            serviceCollection.AddSingleton<IPersonImageRefillStateStore>((ctx) =>
            {
                return new FilePersonImageRefillStateStore(
                    Path.Combine(dataFolderPath, "person-image-refill-state.json"),
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
            serviceCollection.AddSingleton<IPersonMissingImageRefillService, PersonMissingImageRefillService>();
            serviceCollection.AddSingleton<IMissingMetadataSearchService, MissingMetadataSearchService>();
            serviceCollection.AddSingleton<MetaSharkOrdinaryItemLibraryCapabilityResolver>();
            serviceCollection.AddSingleton<MetaSharkSharedEntityLibraryCapabilityResolver>();
            serviceCollection.AddSingleton<SeriesTmdbProviderIdMigrationService>();
            serviceCollection.AddSingleton<IMovieSeriesPeopleOverwriteRefreshCandidateStore>((_) => InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore.Shared);
            serviceCollection.AddSingleton<IEpisodeTitleBackfillCandidateStore, InMemoryEpisodeTitleBackfillCandidateStore>();
            serviceCollection.AddSingleton<IEpisodeTitleBackfillPendingResolver, EpisodeTitleBackfillPendingResolver>();
            serviceCollection.AddSingleton<IEpisodeTitleBackfillPersistence, JellyfinEpisodeTitleBackfillPersistence>();
            serviceCollection.AddSingleton<IEpisodeTitleBackfillPostProcessService>((ctx) =>
            {
                return new EpisodeTitleBackfillPostProcessService(
                    ctx.GetRequiredService<IEpisodeTitleBackfillCandidateStore>(),
                    ctx.GetRequiredService<IEpisodeTitleBackfillPendingResolver>(),
                    ctx.GetRequiredService<IEpisodeTitleBackfillPersistence>(),
                    ctx.GetRequiredService<ILogger<EpisodeTitleBackfillPostProcessService>>(),
                    ctx.GetRequiredService<MetaSharkOrdinaryItemLibraryCapabilityResolver>());
            });
            serviceCollection.AddSingleton<MovieSeriesPeopleRefreshStatePostProcessService>((ctx) =>
            {
                return new MovieSeriesPeopleRefreshStatePostProcessService(
                    ctx.GetRequiredService<ILogger<MovieSeriesPeopleRefreshStatePostProcessService>>(),
                    ctx.GetRequiredService<IPeopleRefreshStateStore>(),
                    ctx.GetRequiredService<IProviderManager>(),
                    ctx.GetRequiredService<IMovieSeriesPeopleOverwriteRefreshCandidateStore>(),
                    ctx.GetRequiredService<IFileSystem>(),
                    ctx.GetRequiredService<ILibraryManager>(),
                    ctx.GetRequiredService<MetaSharkOrdinaryItemLibraryCapabilityResolver>(),
                    ctx.GetRequiredService<MetaSharkSharedEntityLibraryCapabilityResolver>());
            });
            serviceCollection.AddSingleton<IEpisodeOverviewCleanupCandidateStore, InMemoryEpisodeOverviewCleanupCandidateStore>();
            serviceCollection.AddSingleton<IEpisodeOverviewCleanupPendingResolver, EpisodeOverviewCleanupPendingResolver>();
            serviceCollection.AddSingleton<IEpisodeOverviewCleanupPersistence, JellyfinEpisodeOverviewCleanupPersistence>();
            serviceCollection.AddSingleton<IEpisodeOverviewCleanupPostProcessService>((ctx) =>
            {
                return new EpisodeOverviewCleanupPostProcessService(
                    ctx.GetRequiredService<IEpisodeOverviewCleanupCandidateStore>(),
                    ctx.GetRequiredService<IEpisodeOverviewCleanupPendingResolver>(),
                    ctx.GetRequiredService<IEpisodeOverviewCleanupPersistence>(),
                    ctx.GetRequiredService<ILogger<EpisodeOverviewCleanupPostProcessService>>(),
                    ctx.GetRequiredService<MetaSharkOrdinaryItemLibraryCapabilityResolver>());
            });
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
