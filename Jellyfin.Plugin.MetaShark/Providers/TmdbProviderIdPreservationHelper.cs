// <copyright file="TmdbProviderIdPreservationHelper.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Model.Entities;
    using Microsoft.Extensions.Logging;

    public static class TmdbProviderIdPreservationHelper
    {
        private static readonly Action<ILogger, string, Guid, Exception?> LogTmdbProviderIdRejected =
            LoggerMessage.Define<string, Guid>(LogLevel.Warning, new EventId(1, nameof(PreserveTmdbId)), "[MetaShark] 保留 TMDb ProviderId 被拒绝. tmdbId={TmdbId} itemId={ItemId}");

        public static void PreserveMovieTmdbId(string? originalTmdbId, Movie? item, bool hasVerifiedCorrection, ILogger? logger = null)
        {
            PreserveTmdbId(originalTmdbId, item, hasVerifiedCorrection, logger);
        }

        public static void PreserveSeriesTmdbId(string? originalTmdbId, Series? item, bool hasVerifiedCorrection, ILogger? logger = null)
        {
            PreserveTmdbId(originalTmdbId, item, hasVerifiedCorrection, logger);
        }

        private static void PreserveTmdbId(string? originalTmdbId, IHasProviderIds? item, bool hasVerifiedCorrection, ILogger? logger)
        {
            if (hasVerifiedCorrection || item == null || string.IsNullOrWhiteSpace(originalTmdbId))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(item.GetProviderId(MetadataProvider.Tmdb)))
            {
                return;
            }

            var tmdbId = originalTmdbId.Trim();
            if (!item.TrySetProviderId(MetadataProvider.Tmdb, tmdbId) && logger != null && item is BaseItem baseItem)
            {
                LogTmdbProviderIdRejected(logger, tmdbId, baseItem.Id, null);
            }
        }
    }
}
