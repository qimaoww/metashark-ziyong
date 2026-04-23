// <copyright file="EpisodeTitleBackfillCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill
{
    using System;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using Microsoft.Extensions.Logging;

    internal sealed class EpisodeTitleBackfillCoordinator
    {
        private readonly IEpisodeTitleBackfillCandidateStore candidateStore;
        private readonly ILogger logger;

        public EpisodeTitleBackfillCoordinator(IEpisodeTitleBackfillCandidateStore candidateStore, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(candidateStore);
            ArgumentNullException.ThrowIfNull(logger);

            this.candidateStore = candidateStore;
            this.logger = logger;
        }

#pragma warning disable CA1848
        public SearchMissingMetadataTitleBackfillReason Process(EpisodeTitleBackfillOrchestrationInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            var backfillReason = EpisodeTitleBackfillRefreshClassifier.ResolveSearchMissingMetadataTitleBackfillReason(
                input.FeatureEnabled,
                input.IsSearchMissingMetadataRequest,
                input.ItemId,
                input.OriginalMetadataTitle,
                input.EffectiveProviderTitle,
                input.ResolvedTitle);

            if (input.IsSearchMissingMetadataRequest)
            {
                this.LogSearchMissingMetadataTitleBackfillInputs(
                    input.ItemId,
                    input.ItemPath,
                    input.LookupLanguage,
                    input.TitleMetadataLanguage,
                    input.EpisodePreferredLanguage,
                    input.SeriesPreferredLanguage,
                    input.SeasonPreferredLanguage,
                    input.DetailsTitle,
                    input.TranslationTitle,
                    input.EffectiveProviderTitle,
                    input.IsSearchMissingMetadataRequest);
            }

            if (backfillReason == SearchMissingMetadataTitleBackfillReason.CandidateQueued)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var originalTitleSnapshot = (input.OriginalMetadataTitle ?? string.Empty).Trim();
                var candidateTitle = (input.ResolvedTitle ?? string.Empty).Trim();

                this.candidateStore.Save(new EpisodeTitleBackfillCandidate
                {
                    ItemId = input.ItemId,
                    ItemPath = input.ItemPath,
                    OriginalTitleSnapshot = originalTitleSnapshot,
                    CandidateTitle = candidateTitle,
                    QueuedAtUtc = nowUtc,
                    NextAttemptAtUtc = nowUtc.AddSeconds(10),
                    AttemptCount = 0,
                    ExpiresAtUtc = nowUtc.AddMinutes(2),
                });

                this.LogSearchMissingMetadataTitleBackfillQueued(
                    input.ItemId,
                    input.ItemPath,
                    originalTitleSnapshot,
                    candidateTitle,
                    input.LoggedMetadataRefreshMode,
                    input.LoggedReplaceAllMetadata,
                    input.LiveVisible);
            }

            this.LogSearchMissingMetadataTitleBackfillDecision(
                backfillReason.ToString(),
                input.ItemId,
                input.ItemPath,
                (input.OriginalMetadataTitle ?? string.Empty).Trim(),
                (input.ResolvedTitle ?? string.Empty).Trim(),
                input.LoggedMetadataRefreshMode,
                input.LoggedReplaceAllMetadata,
                input.LiveVisible);

            return backfillReason;
        }

        private void LogSearchMissingMetadataTitleBackfillInputs(Guid itemId, string itemPath, string? lookupLanguage, string? titleMetadataLanguage, string? episodePreferredLanguage, string? seriesPreferredLanguage, string? seasonPreferredLanguage, EpisodeLocalizedValue? detailsTitle, EpisodeLocalizedValue? translationTitle, EpisodeLocalizedValue? effectiveProviderTitle, bool isSearchMissingMetadataRequest)
        {
            this.logger.LogInformation(
                "[MetaShark] 剧集标题回填输入. itemId={ItemId} itemPath={ItemPath} lookupLanguage={LookupLanguage} titleMetadataLanguage={TitleMetadataLanguage} episodePreferredLanguage={EpisodePreferredLanguage} seriesPreferredLanguage={SeriesPreferredLanguage} seasonPreferredLanguage={SeasonPreferredLanguage} detailsTitle={DetailsTitle} detailsTitleSourceLanguage={DetailsTitleSourceLanguage} translationTitle={TranslationTitle} translationTitleSourceLanguage={TranslationTitleSourceLanguage} effectiveProviderTitle={EffectiveProviderTitle} effectiveProviderTitleSourceLanguage={EffectiveProviderTitleSourceLanguage} isSearchMissingMetadataRequest={IsSearchMissingMetadataRequest}.",
                itemId,
                itemPath,
                lookupLanguage ?? string.Empty,
                titleMetadataLanguage ?? string.Empty,
                episodePreferredLanguage ?? string.Empty,
                seriesPreferredLanguage ?? string.Empty,
                seasonPreferredLanguage ?? string.Empty,
                detailsTitle?.Value ?? string.Empty,
                detailsTitle?.SourceLanguage ?? string.Empty,
                translationTitle?.Value ?? string.Empty,
                translationTitle?.SourceLanguage ?? string.Empty,
                effectiveProviderTitle?.Value ?? string.Empty,
                effectiveProviderTitle?.SourceLanguage ?? string.Empty,
                isSearchMissingMetadataRequest);
        }

        private void LogSearchMissingMetadataTitleBackfillQueued(Guid itemId, string itemPath, string originalTitle, string candidateTitle, string? metadataRefreshMode, string? replaceAllMetadata, bool liveVisible)
        {
            this.logger.Log(
                liveVisible ? LogLevel.Information : LogLevel.Debug,
                "[MetaShark] 已排队剧集标题回填. itemId={ItemId} itemPath={ItemPath} originalTitle={OriginalTitle} candidateTitle={CandidateTitle} metadataRefreshMode={MetadataRefreshMode} replaceAllMetadata={ReplaceAllMetadata}.",
                itemId,
                itemPath,
                originalTitle,
                candidateTitle,
                metadataRefreshMode ?? string.Empty,
                replaceAllMetadata ?? string.Empty);
        }

        private void LogSearchMissingMetadataTitleBackfillDecision(string reason, Guid itemId, string itemPath, string originalTitle, string resolvedTitle, string? metadataRefreshMode, string? replaceAllMetadata, bool liveVisible)
        {
            this.logger.Log(
                liveVisible ? LogLevel.Information : LogLevel.Debug,
                "[MetaShark] 剧集标题回填决策. reason={Reason} itemId={ItemId} itemPath={ItemPath} originalTitle={OriginalTitle} resolvedTitle={ResolvedTitle} metadataRefreshMode={MetadataRefreshMode} replaceAllMetadata={ReplaceAllMetadata}.",
                reason,
                itemId,
                itemPath,
                originalTitle,
                resolvedTitle,
                metadataRefreshMode ?? string.Empty,
                replaceAllMetadata ?? string.Empty);
        }

        internal sealed record EpisodeTitleBackfillOrchestrationInput(
            bool FeatureEnabled,
            Guid ItemId,
            string ItemPath,
            string? OriginalMetadataTitle,
            EpisodeLocalizedValue? EffectiveProviderTitle,
            string? ResolvedTitle,
            bool IsSearchMissingMetadataRequest,
            string? LoggedMetadataRefreshMode,
            string? LoggedReplaceAllMetadata,
            bool LiveVisible,
            string? LookupLanguage,
            string? TitleMetadataLanguage,
            string? EpisodePreferredLanguage,
            string? SeriesPreferredLanguage,
            string? SeasonPreferredLanguage,
            EpisodeLocalizedValue? DetailsTitle,
            EpisodeLocalizedValue? TranslationTitle);
#pragma warning restore CA1848
    }
}
