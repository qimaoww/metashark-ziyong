// <copyright file="EpisodeTitleBackfillDeferredRetryWorker.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Entities;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    public sealed class EpisodeTitleBackfillDeferredRetryWorker : IHostedService, IDisposable
    {
        private const int MaxItemsPerCycle = 20;
        private static readonly TimeSpan RetryScanInterval = TimeSpan.FromSeconds(5);

        private static readonly Action<ILogger, Exception?> LogWorkerStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(StartAsync)), "Starting episode-title-backfill deferred-retry worker.");

        private static readonly Action<ILogger, Exception?> LogWorkerStop =
            LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(StopAsync)), "Stopping episode-title-backfill deferred-retry worker.");

        private static readonly Action<ILogger, Exception?> LogCycleFailed =
            LoggerMessage.Define(LogLevel.Error, new EventId(3, nameof(ExecuteLoopAsync)), "Episode title backfill deferred-retry cycle failed.");

        private static readonly Action<ILogger, Guid, string, Exception?> LogRetryFailed =
            LoggerMessage.Define<Guid, string>(LogLevel.Error, new EventId(4, nameof(ProcessCandidateAsync)), "Episode title backfill deferred-retry apply failed for item {ItemId} path {ItemPath} trigger=DeferredRetry.");

        private readonly IEpisodeTitleBackfillCandidateStore candidateStore;
        private readonly IEpisodeTitleBackfillPendingResolver pendingResolver;
        private readonly IEpisodeTitleBackfillPostProcessService postProcessService;
        private readonly ILogger<EpisodeTitleBackfillDeferredRetryWorker> logger;
        private CancellationTokenSource? executionCancellationTokenSource;
        private Task? executionTask;

        public EpisodeTitleBackfillDeferredRetryWorker(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            IEpisodeTitleBackfillPendingResolver pendingResolver,
            IEpisodeTitleBackfillPostProcessService postProcessService,
            ILogger<EpisodeTitleBackfillDeferredRetryWorker> logger)
        {
            this.candidateStore = candidateStore;
            this.pendingResolver = pendingResolver;
            this.postProcessService = postProcessService;
            this.logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (this.executionTask != null)
            {
                return Task.CompletedTask;
            }

            LogWorkerStart(this.logger, null);
            this.executionCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            this.executionTask = Task.Run(() => this.ExecuteLoopAsync(this.executionCancellationTokenSource.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (this.executionCancellationTokenSource == null || this.executionTask == null)
            {
                return;
            }

            LogWorkerStop(this.logger, null);
            await this.executionCancellationTokenSource.CancelAsync().ConfigureAwait(false);

            try
            {
                await this.executionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                this.executionTask = null;
                this.executionCancellationTokenSource.Dispose();
                this.executionCancellationTokenSource = null;
            }
        }

        public void Dispose()
        {
            this.executionCancellationTokenSource?.Cancel();
            this.executionCancellationTokenSource?.Dispose();
        }

        internal async Task ExecuteDueCycleAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            var dueCandidates = this.candidateStore.GetDueDeferredRetries(nowUtc, MaxItemsPerCycle);
            foreach (var candidate in dueCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await this.ProcessCandidateAsync(candidate, nowUtc, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ExecuteLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(RetryScanInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await this.ExecuteDueCycleAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031
                catch (Exception ex)
                {
                    LogCycleFailed(this.logger, ex);
                }
#pragma warning restore CA1031
            }
        }

        private async Task ProcessCandidateAsync(EpisodeTitleBackfillCandidate candidate, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            if (candidate.ExpiresAtUtc <= nowUtc)
            {
                this.pendingResolver.Expire(candidate);
                return;
            }

            var episode = this.pendingResolver.ResolveCurrentEpisode(candidate);
            if (episode == null)
            {
                this.pendingResolver.MarkDeferredAttempt(candidate, nowUtc);
                return;
            }

            var currentTitle = (episode.Name ?? string.Empty).Trim();
            if (!EpisodeProvider.IsDefaultJellyfinEpisodeTitle(currentTitle))
            {
                this.pendingResolver.Complete(candidate);
                return;
            }

            this.pendingResolver.MarkDeferredAttempt(candidate, nowUtc);

            try
            {
                await this.postProcessService.TryApplyAsync(
                    new ItemChangeEventArgs
                    {
                        Item = episode,
                        UpdateReason = ItemUpdateType.MetadataDownload,
                    },
                    IEpisodeTitleBackfillPostProcessService.DeferredRetryTrigger,
                    cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                LogRetryFailed(this.logger, episode.Id, candidate.ItemPath, ex);
            }
#pragma warning restore CA1031
        }
    }
}
