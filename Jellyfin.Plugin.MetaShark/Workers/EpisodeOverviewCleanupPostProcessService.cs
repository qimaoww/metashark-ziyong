// <copyright file="EpisodeOverviewCleanupPostProcessService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Entities;
    using Microsoft.Extensions.Logging;

    public sealed class EpisodeOverviewCleanupPostProcessService : IEpisodeOverviewCleanupPostProcessService
    {
        private readonly IEpisodeOverviewCleanupPendingResolver pendingResolver;
        private readonly IEpisodeOverviewCleanupPersistence persistence;
        private readonly ILogger<EpisodeOverviewCleanupPostProcessService> logger;

        private static bool IsAcceptedUpdateReason(ItemUpdateType updateReason)
        {
            return updateReason.HasFlag(ItemUpdateType.MetadataImport)
                || updateReason.HasFlag(ItemUpdateType.MetadataDownload);
        }

#pragma warning disable SA1201
        public EpisodeOverviewCleanupPostProcessService(
            IEpisodeOverviewCleanupCandidateStore candidateStore,
            IEpisodeOverviewCleanupPersistence persistence,
            ILogger<EpisodeOverviewCleanupPostProcessService> logger)
            : this(candidateStore, new EpisodeOverviewCleanupPendingResolver(candidateStore), persistence, logger)
        {
        }

        public EpisodeOverviewCleanupPostProcessService(
            IEpisodeOverviewCleanupCandidateStore candidateStore,
            IEpisodeOverviewCleanupPendingResolver pendingResolver,
            IEpisodeOverviewCleanupPersistence persistence,
            ILogger<EpisodeOverviewCleanupPostProcessService> logger)
        {
            ArgumentNullException.ThrowIfNull(candidateStore);
            ArgumentNullException.ThrowIfNull(pendingResolver);
            ArgumentNullException.ThrowIfNull(persistence);
            ArgumentNullException.ThrowIfNull(logger);

            this.pendingResolver = pendingResolver;
            this.persistence = persistence;
            this.logger = logger;
        }
#pragma warning restore SA1201

#pragma warning disable CA1848
        public async Task TryApplyAsync(ItemChangeEventArgs e, string triggerName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(e);
            ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
            cancellationToken.ThrowIfCancellationRequested();

            ValidateTriggerName(triggerName);

            if (e.Item is not Episode episode)
            {
                return;
            }

            if (episode.Id == Guid.Empty)
            {
                return;
            }

            if (!IsAcceptedUpdateReason(e.UpdateReason))
            {
                return;
            }

            var claimToken = Guid.NewGuid().ToString("N");
            var candidate = this.pendingResolver.TryClaimForUpdatedEpisode(episode, claimToken);
            if (candidate == null)
            {
                return;
            }

            var originalOverviewSnapshot = (candidate.OriginalOverviewSnapshot ?? string.Empty).Trim();
            var currentOverview = (episode.Overview ?? string.Empty).Trim();

            if (episode.IsLocked || episode.LockedFields?.Contains(MetadataField.Overview) == true)
            {
                this.pendingResolver.Complete(candidate);
                return;
            }

            if (string.IsNullOrWhiteSpace(currentOverview))
            {
                this.pendingResolver.Complete(candidate);
                return;
            }

            if (!string.IsNullOrWhiteSpace(originalOverviewSnapshot))
            {
                this.pendingResolver.Complete(candidate);
                return;
            }

            var originalEpisodeOverview = episode.Overview;
            episode.Overview = null;
            try
            {
                await this.persistence.SaveAsync(episode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                episode.Overview = originalEpisodeOverview;
                this.pendingResolver.ReleaseClaim(candidate, claimToken);
                this.logger.LogError(
                    ex,
                    "[MetaShark] 剧集简介清理保存失败. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} currentOverview={CurrentOverview} updateReason={UpdateReason}.",
                    episode.Id,
                    triggerName,
                    episode.Path ?? string.Empty,
                    currentOverview,
                    e.UpdateReason);
                throw;
            }

            this.pendingResolver.Complete(candidate);
            this.logger.LogDebug(
                "[MetaShark] 已应用剧集简介清理. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} currentOverviewLength={CurrentOverviewLength} updateReason={UpdateReason}.",
                episode.Id,
                triggerName,
                episode.Path ?? string.Empty,
                currentOverview.Length,
                e.UpdateReason);
        }
#pragma warning restore CA1848

        private static void ValidateTriggerName(string triggerName)
        {
            if (string.Equals(triggerName, IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, StringComparison.Ordinal)
                || string.Equals(triggerName, IEpisodeOverviewCleanupPostProcessService.DeferredRetryTrigger, StringComparison.Ordinal))
            {
                return;
            }

            throw new ArgumentOutOfRangeException(nameof(triggerName), triggerName, "Only ItemUpdated and DeferredRetry triggers are supported.");
        }
    }
}
