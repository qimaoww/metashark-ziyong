// <copyright file="EpisodeGroupSeasonRefreshTask.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.ScheduledTasks
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Tasks;
    using Microsoft.Extensions.Logging;

    public sealed class EpisodeGroupSeasonRefreshTask : IScheduledTask
    {
        private static readonly Action<ILogger, Exception?> LogStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(ExecuteAsync)), "[MetaShark] 开始刷新剧集组映射季.");

        private static readonly Action<ILogger, Exception?> LogNoMappings =
            LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(ExecuteAsync)), "[MetaShark] 未找到有效剧集组映射，跳过季刷新.");

        private static readonly Action<ILogger, Exception?> LogNoTargets =
            LoggerMessage.Define(LogLevel.Information, new EventId(3, nameof(ExecuteAsync)), "[MetaShark] 未找到可刷新的剧集组映射季.");

        private static readonly Action<ILogger, string, Exception?> LogSkipEmptyId =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, nameof(ExecuteAsync)), "[MetaShark] 跳过剧集组映射季刷新，条目 ID 为空. name={Name}.");

        private static readonly Action<ILogger, int, int, Exception?> LogFinished =
            LoggerMessage.Define<int, int>(LogLevel.Information, new EventId(5, nameof(ExecuteAsync)), "[MetaShark] 剧集组映射季刷新排队完成. Queued={Queued} Plans={Plans}.");

        private readonly ILogger<EpisodeGroupSeasonRefreshTask> logger;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly Func<PluginConfiguration?> configurationProvider;
        private readonly IEpisodeGroupMappingFacade episodeGroupMappingFacade;
        private readonly EpisodeGroupRefreshService refreshService;

        public EpisodeGroupSeasonRefreshTask(
            ILogger<EpisodeGroupSeasonRefreshTask> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem)
            : this(
                logger,
                libraryManager,
                providerManager,
                fileSystem,
                () => MetaSharkPlugin.Instance?.Configuration)
        {
        }

        internal EpisodeGroupSeasonRefreshTask(
            ILogger<EpisodeGroupSeasonRefreshTask> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            Func<PluginConfiguration?> configurationProvider)
            : this(
                logger,
                libraryManager,
                providerManager,
                fileSystem,
                configurationProvider,
                new EpisodeGroupMappingFacade(),
                new EpisodeGroupRefreshService())
        {
        }

        internal EpisodeGroupSeasonRefreshTask(
            ILogger<EpisodeGroupSeasonRefreshTask> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            Func<PluginConfiguration?> configurationProvider,
            IEpisodeGroupMappingFacade episodeGroupMappingFacade,
            EpisodeGroupRefreshService refreshService)
        {
            this.logger = logger;
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.configurationProvider = configurationProvider;
            this.episodeGroupMappingFacade = episodeGroupMappingFacade;
            this.refreshService = refreshService;
        }

        public string Key => $"{MetaSharkPlugin.PluginName}RefreshEpisodeGroupSeasons";

        public string Name => "刷新剧集组映射季";

        public string Description => "对已配置 TMDb 剧集组映射的剧集逐季扫描新的和有修改的文件";

        public string Category => MetaSharkPlugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Enumerable.Empty<TaskTriggerInfo>();
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            LogStart(this.logger, null);

            var configuration = this.configurationProvider();
            var mappingText = this.episodeGroupMappingFacade.GetEffectiveMappingText(configuration);
            var snapshot = this.refreshService.ParseSnapshot(mappingText);
            if (snapshot.MappedSeriesIds.Count == 0)
            {
                LogNoMappings(this.logger, null);
                progress.Report(100);
                return Task.CompletedTask;
            }

            var mappedSeriesIds = new HashSet<string>(snapshot.MappedSeriesIds, StringComparer.OrdinalIgnoreCase);
            var queueablePlans = EpisodeGroupRefreshQueueSelector.SelectQueueableRefreshPlans(
                this.libraryManager,
                this.fileSystem,
                mappedSeriesIds,
                GetTmdbSeriesId);

            if (queueablePlans.Count == 0)
            {
                LogNoTargets(this.logger, null);
                progress.Report(100);
                return Task.CompletedTask;
            }

            var queued = 0;
            var processed = 0;
            foreach (var plan in queueablePlans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                queued += EpisodeGroupRefreshQueueSelector.QueueRefreshTargets(
                    plan,
                    this.providerManager,
                    this.fileSystem,
                    item => LogSkipEmptyId(this.logger, item.Name ?? string.Empty, null));
                processed++;
                progress.Report(processed * 100.0 / queueablePlans.Count);
            }

            LogFinished(this.logger, queued, queueablePlans.Count, null);
            return Task.CompletedTask;
        }

        private static string? GetTmdbSeriesId(BaseItem item)
        {
            return item.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId) ? tmdbId : null;
        }
    }
}
