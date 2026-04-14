// <copyright file="EpisodeTitleBackfillPostProcessService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Entities;
    using Microsoft.Extensions.Logging;

    public sealed class EpisodeTitleBackfillPostProcessService : IEpisodeTitleBackfillPostProcessService
    {
        private static readonly Action<ILogger, Guid, Exception?> LogProcessUpdatedItem =
            LoggerMessage.Define<Guid>(LogLevel.Debug, new EventId(1, nameof(ProcessUpdatedItemAsync)), "Received episode title backfill post-process event for {ItemId}.");

        private static readonly Action<ILogger, Guid, string, string, ItemUpdateType, Exception?> LogSkipFeatureDisabled =
            LoggerMessage.Define<Guid, string, string, ItemUpdateType>(LogLevel.Debug, new EventId(2, nameof(LogSkipFeatureDisabled)), "Skipping episode title backfill for {ItemId} because feature is disabled. currentTitle={CurrentTitle} candidateTitle={CandidateTitle} updateReason={UpdateReason}.");

        private static readonly Action<ILogger, Guid, ItemUpdateType, string, string, Exception?> LogSkipUpdateReason =
            LoggerMessage.Define<Guid, ItemUpdateType, string, string>(LogLevel.Debug, new EventId(3, nameof(LogSkipUpdateReason)), "Skipping episode title backfill for {ItemId} because update reason is {UpdateReason}. currentTitle={CurrentTitle} candidateTitle={CandidateTitle}.");

        private static readonly Action<ILogger, Guid, string, string, ItemUpdateType, Exception?> LogSkipNoCandidate =
            LoggerMessage.Define<Guid, string, string, ItemUpdateType>(LogLevel.Debug, new EventId(4, nameof(LogSkipNoCandidate)), "No candidate available for episode title backfill item {ItemId}. currentTitle={CurrentTitle} candidateTitle={CandidateTitle} updateReason={UpdateReason}.");

        private static readonly Action<ILogger, Guid, string, string, ItemUpdateType, Exception?> LogSkipLocked =
            LoggerMessage.Define<Guid, string, string, ItemUpdateType>(LogLevel.Debug, new EventId(5, nameof(LogSkipLocked)), "Skipping episode title backfill for {ItemId} because episode is locked. currentTitle={CurrentTitle} candidateTitle={CandidateTitle} updateReason={UpdateReason}.");

        private static readonly Action<ILogger, Guid, string, string, string, ItemUpdateType, Exception?> LogSkipTitleSnapshotMismatch =
            LoggerMessage.Define<Guid, string, string, string, ItemUpdateType>(LogLevel.Debug, new EventId(6, nameof(LogSkipTitleSnapshotMismatch)), "Skipping episode title backfill for {ItemId} because current title {CurrentTitle} does not match original snapshot {OriginalTitleSnapshot}. candidateTitle={CandidateTitle} updateReason={UpdateReason}.");

        private static readonly Action<ILogger, Guid, string, string, ItemUpdateType, Exception?> LogSkipCurrentTitleNotDefault =
            LoggerMessage.Define<Guid, string, string, ItemUpdateType>(LogLevel.Debug, new EventId(7, nameof(LogSkipCurrentTitleNotDefault)), "Skipping episode title backfill for {ItemId} because current title {CurrentTitle} is not the default Jellyfin episode title. candidateTitle={CandidateTitle} updateReason={UpdateReason}.");

        private static readonly Action<ILogger, Guid, string, string, ItemUpdateType, Exception?> LogSkipCurrentEqualsCandidate =
            LoggerMessage.Define<Guid, string, string, ItemUpdateType>(LogLevel.Debug, new EventId(8, nameof(LogSkipCurrentEqualsCandidate)), "Skipping episode title backfill for {ItemId} because current title {CurrentTitle} already matches candidate title {CandidateTitle}. updateReason={UpdateReason}.");

        private static readonly Action<ILogger, Guid, string, string, ItemUpdateType, Exception?> LogApplySuccess =
            LoggerMessage.Define<Guid, string, string, ItemUpdateType>(LogLevel.Information, new EventId(9, nameof(LogApplySuccess)), "Applied episode title backfill for {ItemId}. currentTitle={CurrentTitle} candidateTitle={CandidateTitle} updateReason={UpdateReason}.");

        private readonly IEpisodeTitleBackfillCandidateStore candidateStore;
        private readonly IEpisodeTitleBackfillPersistence persistence;
        private readonly ILogger<EpisodeTitleBackfillPostProcessService> logger;

        public EpisodeTitleBackfillPostProcessService(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            IEpisodeTitleBackfillPersistence persistence,
            ILogger<EpisodeTitleBackfillPostProcessService> logger)
        {
            this.candidateStore = candidateStore;
            this.persistence = persistence;
            this.logger = logger;
        }

        public async Task ProcessUpdatedItemAsync(ItemChangeEventArgs e, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(e);
            cancellationToken.ThrowIfCancellationRequested();

            if (e.Item is not Episode episode)
            {
                return;
            }

            if (episode.Id == Guid.Empty)
            {
                return;
            }

            LogProcessUpdatedItem(this.logger, episode.Id, null);
            var currentTitle = (episode.Name ?? string.Empty).Trim();

            if (!(MetaSharkPlugin.Instance?.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill ?? false))
            {
                this.candidateStore.Remove(episode.Id);
                LogSkipFeatureDisabled(this.logger, episode.Id, currentTitle, string.Empty, e.UpdateReason, null);
                return;
            }

            if (!e.UpdateReason.HasFlag(ItemUpdateType.MetadataImport))
            {
                LogSkipUpdateReason(this.logger, episode.Id, e.UpdateReason, currentTitle, string.Empty, null);
                return;
            }

            var candidate = this.candidateStore.Consume(episode.Id, DateTimeOffset.UtcNow);
            if (candidate == null)
            {
                LogSkipNoCandidate(this.logger, episode.Id, currentTitle, string.Empty, e.UpdateReason, null);
                return;
            }

            var originalTitleSnapshot = (candidate.OriginalTitleSnapshot ?? string.Empty).Trim();
            var candidateTitle = (candidate.CandidateTitle ?? string.Empty).Trim();

            if (episode.IsLocked || episode.LockedFields.Contains(MetadataField.Name))
            {
                LogSkipLocked(this.logger, episode.Id, currentTitle, candidateTitle, e.UpdateReason, null);
                return;
            }

            if (!string.Equals(currentTitle, originalTitleSnapshot, StringComparison.Ordinal))
            {
                LogSkipTitleSnapshotMismatch(this.logger, episode.Id, currentTitle, originalTitleSnapshot, candidateTitle, e.UpdateReason, null);
                return;
            }

            if (!EpisodeProvider.IsDefaultJellyfinEpisodeTitle(currentTitle))
            {
                LogSkipCurrentTitleNotDefault(this.logger, episode.Id, currentTitle, candidateTitle, e.UpdateReason, null);
                return;
            }

            if (string.Equals(currentTitle, candidateTitle, StringComparison.Ordinal))
            {
                LogSkipCurrentEqualsCandidate(this.logger, episode.Id, currentTitle, candidateTitle, e.UpdateReason, null);
                return;
            }

            episode.Name = candidate.CandidateTitle;
            await this.persistence.SaveAsync(episode, cancellationToken).ConfigureAwait(false);
            LogApplySuccess(this.logger, episode.Id, currentTitle, candidateTitle, e.UpdateReason, null);
        }
    }
}
