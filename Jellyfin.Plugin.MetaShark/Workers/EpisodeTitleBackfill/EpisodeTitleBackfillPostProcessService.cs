// <copyright file="EpisodeTitleBackfillPostProcessService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Entities;
    using Microsoft.Extensions.Logging;

    public sealed class EpisodeTitleBackfillPostProcessService : IEpisodeTitleBackfillPostProcessService
    {
        private readonly IEpisodeTitleBackfillPendingResolver pendingResolver;
        private readonly IEpisodeTitleBackfillPersistence persistence;
        private readonly ILogger<EpisodeTitleBackfillPostProcessService> logger;

        private static bool IsAcceptedUpdateReason(ItemUpdateType updateReason)
        {
            return updateReason.HasFlag(ItemUpdateType.MetadataImport)
                || updateReason.HasFlag(ItemUpdateType.MetadataDownload);
        }

        private static bool PathMatches(string left, string right)
        {
            return string.Equals(
                left ?? string.Empty,
                right ?? string.Empty,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

#pragma warning disable SA1201
        public EpisodeTitleBackfillPostProcessService(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            IEpisodeTitleBackfillPersistence persistence,
            ILogger<EpisodeTitleBackfillPostProcessService> logger)
            : this(candidateStore, new EpisodeTitleBackfillPendingResolver(candidateStore), persistence, logger)
        {
        }

        public EpisodeTitleBackfillPostProcessService(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            IEpisodeTitleBackfillPendingResolver pendingResolver,
            IEpisodeTitleBackfillPersistence persistence,
            ILogger<EpisodeTitleBackfillPostProcessService> logger)
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

            this.logger.LogDebug(
                "[MetaShark] 收到剧集标题回填后处理事件. trigger={Trigger} itemId={ItemId} itemPath={ItemPath} updateReason={UpdateReason}.",
                triggerName,
                episode.Id,
                episode.Path ?? string.Empty,
                e.UpdateReason);
            var claimToken = Guid.NewGuid().ToString("N");
            var currentTitle = (episode.Name ?? string.Empty).Trim();

            if (!(MetaSharkPlugin.Instance?.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill ?? false))
            {
                this.LogSkip("FeatureDisabled", triggerName, episode, currentTitle, string.Empty, e.UpdateReason, null);
                return;
            }

            if (!IsAcceptedUpdateReason(e.UpdateReason))
            {
                this.LogSkip("UpdateReasonRejected", triggerName, episode, currentTitle, string.Empty, e.UpdateReason, null);
                return;
            }

            var candidate = this.pendingResolver.TryClaimForUpdatedEpisode(episode, claimToken);
            if (candidate == null)
            {
                this.LogSkip("NoCandidate", triggerName, episode, currentTitle, string.Empty, e.UpdateReason, null);
                return;
            }

            var originalTitleSnapshot = (candidate.OriginalTitleSnapshot ?? string.Empty).Trim();
            var candidateTitle = (candidate.CandidateTitle ?? string.Empty).Trim();
            var itemPath = string.IsNullOrWhiteSpace(candidate.ItemPath) ? episode.Path ?? string.Empty : candidate.ItemPath;

            if (episode.IsLocked || episode.LockedFields?.Contains(MetadataField.Name) == true)
            {
                this.pendingResolver.Complete(candidate);
                this.LogSkip("Locked", triggerName, episode, currentTitle, candidateTitle, e.UpdateReason, null);
                return;
            }

            if (!string.Equals(currentTitle, originalTitleSnapshot, StringComparison.Ordinal))
            {
                this.pendingResolver.Complete(candidate);
                this.LogSkip("TitleSnapshotMismatch", triggerName, episode, currentTitle, candidateTitle, e.UpdateReason, originalTitleSnapshot);
                return;
            }

            if (!EpisodeProvider.IsDefaultJellyfinEpisodeTitle(currentTitle))
            {
                this.pendingResolver.Complete(candidate);
                this.LogSkip("CurrentTitleNotDefault", triggerName, episode, currentTitle, candidateTitle, e.UpdateReason, null);
                return;
            }

            if (string.Equals(currentTitle, candidateTitle, StringComparison.Ordinal))
            {
                this.pendingResolver.Complete(candidate);
                this.LogSkip("CurrentEqualsCandidate", triggerName, episode, currentTitle, candidateTitle, e.UpdateReason, null);
                return;
            }

            var originalEpisodeName = episode.Name;
            episode.Name = candidate.CandidateTitle;
            try
            {
                await this.persistence.SaveAsync(episode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                episode.Name = originalEpisodeName;
                this.pendingResolver.ReleaseClaim(candidate, claimToken);
                this.logger.LogError(
                    ex,
                    "[MetaShark] 剧集标题回填保存失败. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} currentTitle={CurrentTitle} candidateTitle={CandidateTitle} updateReason={UpdateReason}.",
                    episode.Id,
                    triggerName,
                    itemPath,
                    currentTitle,
                    candidateTitle,
                    e.UpdateReason);
                throw;
            }

            this.pendingResolver.Complete(candidate);
            this.logger.LogInformation(
                "[MetaShark] 已应用剧集标题回填. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} currentTitle={CurrentTitle} candidateTitle={CandidateTitle} updateReason={UpdateReason}.",
                episode.Id,
                triggerName,
                itemPath,
                currentTitle,
                candidateTitle,
                e.UpdateReason);
        }

        private static void ValidateTriggerName(string triggerName)
        {
            if (string.Equals(triggerName, IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger, StringComparison.Ordinal)
                || string.Equals(triggerName, IEpisodeTitleBackfillPostProcessService.DeferredRetryTrigger, StringComparison.Ordinal))
            {
                return;
            }

            throw new ArgumentOutOfRangeException(nameof(triggerName), triggerName, "Only ItemUpdated and DeferredRetry triggers are supported.");
        }

        private void LogSkip(string reason, string triggerName, Episode episode, string currentTitle, string candidateTitle, ItemUpdateType updateReason, string? detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                this.logger.LogInformation(
                    "[MetaShark] 跳过剧集标题回填. reason={Reason} trigger={Trigger} itemId={ItemId} itemPath={ItemPath} currentTitle={CurrentTitle} candidateTitle={CandidateTitle} updateReason={UpdateReason}.",
                    reason,
                    triggerName,
                    episode.Id,
                    episode.Path ?? string.Empty,
                    currentTitle,
                    candidateTitle,
                    updateReason);

                return;
            }

            this.logger.LogInformation(
                "[MetaShark] 跳过剧集标题回填. reason={Reason} trigger={Trigger} itemId={ItemId} itemPath={ItemPath} currentTitle={CurrentTitle} candidateTitle={CandidateTitle} updateReason={UpdateReason} detail={Detail}.",
                reason,
                triggerName,
                episode.Id,
                episode.Path ?? string.Empty,
                currentTitle,
                candidateTitle,
                updateReason,
                detail);
        }
#pragma warning restore CA1848
    }
}
