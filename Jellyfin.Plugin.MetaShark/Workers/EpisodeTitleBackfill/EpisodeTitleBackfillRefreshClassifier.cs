// <copyright file="EpisodeTitleBackfillRefreshClassifier.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill
{
    using System;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using Microsoft.AspNetCore.Http;

    internal static class EpisodeTitleBackfillRefreshClassifier
    {
        internal static bool IsSearchMissingMetadataRefresh(string? metadataRefreshMode, string? replaceAllMetadata)
        {
            return string.Equals(metadataRefreshMode?.Trim(), "FullRefresh", StringComparison.OrdinalIgnoreCase)
                && string.Equals(replaceAllMetadata?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasSearchMissingMetadataRefreshQuery(HttpContext? httpContext)
        {
            var query = httpContext?.Request.Query;
            return query?.ContainsKey("metadataRefreshMode") == true
                || query?.ContainsKey("replaceAllMetadata") == true;
        }

        internal static bool ShouldFallbackSearchMissingMetadataRefresh(string? originalMetadataTitle, string? currentItemTitle)
        {
            var trimmedOriginalTitle = originalMetadataTitle?.Trim();
            var trimmedCurrentItemTitle = currentItemTitle?.Trim();
            return EpisodeTitleBackfillPolicy.IsDefaultJellyfinEpisodeTitle(trimmedOriginalTitle)
                && string.Equals(trimmedOriginalTitle, trimmedCurrentItemTitle, StringComparison.Ordinal);
        }

        internal static SearchMissingMetadataTitleBackfillReason ResolveSearchMissingMetadataTitleBackfillReason(
            bool featureEnabled,
            bool isSearchMissingMetadataRequest,
            Guid itemId,
            string? originalMetadataTitle,
            EpisodeLocalizedValue? providerTitle,
            string? resolvedTitle)
        {
            if (!featureEnabled)
            {
                return SearchMissingMetadataTitleBackfillReason.FeatureDisabled;
            }

            if (!isSearchMissingMetadataRequest)
            {
                return SearchMissingMetadataTitleBackfillReason.RequestNotSearchMissingMetadata;
            }

            if (itemId == Guid.Empty)
            {
                return SearchMissingMetadataTitleBackfillReason.EpisodeIdMissing;
            }

            var trimmedOriginalTitle = originalMetadataTitle?.Trim();
            if (!EpisodeTitleBackfillPolicy.IsDefaultJellyfinEpisodeTitle(trimmedOriginalTitle))
            {
                return SearchMissingMetadataTitleBackfillReason.OriginalTitleNotDefault;
            }

            var trimmedProviderTitle = providerTitle?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedProviderTitle))
            {
                return SearchMissingMetadataTitleBackfillReason.ResolvedTitleEmpty;
            }

            var trimmedResolvedTitle = resolvedTitle?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedResolvedTitle))
            {
                return SearchMissingMetadataTitleBackfillReason.ResolvedTitleEmpty;
            }

            if (string.Equals(trimmedOriginalTitle, trimmedResolvedTitle, StringComparison.Ordinal))
            {
                return IsStrictZhCnRejectedSearchMissingMetadataTitleBackfill(trimmedOriginalTitle, providerTitle)
                    ? SearchMissingMetadataTitleBackfillReason.StrictZhCnRejected
                    : SearchMissingMetadataTitleBackfillReason.ResolvedTitleSameAsOriginal;
            }

            return SearchMissingMetadataTitleBackfillReason.CandidateQueued;
        }

        internal static bool ShouldQueueSearchMissingMetadataTitleBackfill(bool featureEnabled, Guid itemId, string? originalMetadataTitle, string? resolvedTitle)
        {
            return ResolveSearchMissingMetadataTitleBackfillReason(
                featureEnabled,
                true,
                itemId,
                originalMetadataTitle,
                CreateEpisodeLocalizedValue(resolvedTitle, null),
                resolvedTitle) == SearchMissingMetadataTitleBackfillReason.CandidateQueued;
        }

        private static bool IsStrictZhCnRejectedSearchMissingMetadataTitleBackfill(string? originalMetadataTitle, EpisodeLocalizedValue? providerTitle)
        {
            var trimmedProviderTitle = providerTitle?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedProviderTitle)
                || EpisodeTitleBackfillPolicy.IsGenericTmdbEpisodeTitle(trimmedProviderTitle)
                || string.Equals(originalMetadataTitle, trimmedProviderTitle, StringComparison.Ordinal))
            {
                return false;
            }

            return !HasStrictZhCnTitleSource(providerTitle);
        }

        private static bool HasStrictZhCnTitleSource(EpisodeLocalizedValue? providerTitle)
        {
            var normalizedSourceLanguage = string.IsNullOrWhiteSpace(providerTitle?.SourceLanguage)
                ? null
                : ChineseLocalePolicy.CanonicalizeLanguage(providerTitle.SourceLanguage);
            return string.Equals(normalizedSourceLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase);
        }

        private static EpisodeLocalizedValue CreateEpisodeLocalizedValue(string? value, string? sourceLanguage)
        {
            return new EpisodeLocalizedValue
            {
                Value = value,
                SourceLanguage = sourceLanguage,
            };
        }
    }
}
