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

    /// <summary>
    /// 监听剧集组映射配置变更并刷新受影响的剧集。
    /// 配置保存线程上只读取一次映射文本，全库查询、磁盘探测与入队都移到后台串行执行，
    /// 避免把宿主保存配置的 HTTP 请求阻塞在整库扫描上。
    /// </summary>
    public sealed class EpisodeGroupMappingConfigurationRefreshService : IHostedService
    {
        private static readonly Action<ILogger, int, Exception?> LogQueuedRefresh =
            LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, nameof(OnConfigurationChanged)), "[MetaShark] 剧集组映射配置变更已排队刷新. Count={Count}.");

        private static readonly Action<ILogger, Exception?> LogRefreshFailed =
            LoggerMessage.Define(LogLevel.Warning, new EventId(2, nameof(OnConfigurationChanged)), "[MetaShark] 剧集组映射配置变更刷新排队失败.");

        private readonly object syncRoot = new object();
        private readonly IEpisodeGroupMappingFacade episodeGroupMappingFacade;
        private readonly EpisodeGroupRefreshCoordinator episodeGroupRefreshCoordinator;
        private readonly ILogger<EpisodeGroupMappingConfigurationRefreshService> logger;
        private string lastEffectiveMapping = string.Empty;
        private string? pendingEffectiveMapping;
        private bool refreshWorkerRunning;
        private bool stopRequested;
        private Task? refreshWorker;

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

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plugin = MetaSharkPlugin.Instance;
            if (plugin != null)
            {
                plugin.ConfigurationChanged -= this.OnConfigurationChanged;
            }

            Task? worker;
            lock (this.syncRoot)
            {
                // 停止只拒绝新事件；已经排队的刷新仍然处理完，避免配置已保存但剧集没有刷新。
                this.stopRequested = true;
                worker = this.refreshWorker;
            }

            if (worker != null)
            {
                await worker.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 外部路径（例如 LLM 写入映射）在自行刷新完成后同步基线，
        /// 避免下一次配置保存把同一批变更再整队刷新一次。
        /// </summary>
        /// <param name="effectiveMapping">当前生效的映射文本。</param>
        public void SynchronizeEffectiveMappingBaseline(string? effectiveMapping)
        {
            var normalizedMapping = effectiveMapping ?? string.Empty;
            lock (this.syncRoot)
            {
                this.pendingEffectiveMapping = null;
                this.lastEffectiveMapping = normalizedMapping;
            }
        }

        /// <summary>
        /// 等待已排队的配置变更刷新处理完成（供测试在 UpdateConfiguration 之后同步断言）。
        /// </summary>
        internal Task WaitForPendingRefreshAsync()
        {
            lock (this.syncRoot)
            {
                return this.refreshWorker ?? Task.CompletedTask;
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Configuration change event handlers must not throw into Jellyfin configuration saving; failures are logged and the saved mapping remains intact.")]
        private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
        {
            try
            {
                var newEffectiveMapping = this.episodeGroupMappingFacade.GetEffectiveMappingText(configuration as PluginConfiguration);
                lock (this.syncRoot)
                {
                    // 后台只消费「最新一次」有效映射：连续保存时中间态无需逐个刷新。
                    this.pendingEffectiveMapping = newEffectiveMapping;
                    if (this.stopRequested || this.refreshWorkerRunning)
                    {
                        return;
                    }

                    this.refreshWorkerRunning = true;
                    this.refreshWorker = Task.Run(this.RunRefreshWorkerAsync);
                }
            }
            catch (Exception ex)
            {
                LogRefreshFailed(this.logger, ex);
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "后台刷新失败只记录日志，保持旧基线以便下次配置变更重试，且不能影响宿主的后台线程。")]
        private async Task RunRefreshWorkerAsync()
        {
            while (true)
            {
                string newEffectiveMapping;
                lock (this.syncRoot)
                {
                    if (this.pendingEffectiveMapping == null)
                    {
                        this.refreshWorkerRunning = false;
                        return;
                    }

                    newEffectiveMapping = this.pendingEffectiveMapping;
                    this.pendingEffectiveMapping = null;
                }

                try
                {
                    var oldEffectiveMapping = this.lastEffectiveMapping;
                    if (string.Equals(oldEffectiveMapping, newEffectiveMapping, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var outcome = this.episodeGroupRefreshCoordinator.QueueAffectedSeriesRefresh(oldEffectiveMapping, newEffectiveMapping);
                    this.lastEffectiveMapping = newEffectiveMapping;
                    if (outcome.RefreshResult.AffectedSeriesIds.Count > 0)
                    {
                        LogQueuedRefresh(this.logger, outcome.QueuedCount, null);
                    }
                }
                catch (Exception ex)
                {
                    // 基线保持旧值：下次配置变更会重新计算这段差异并重试。
                    LogRefreshFailed(this.logger, ex);
                }
            }
        }
    }
}
