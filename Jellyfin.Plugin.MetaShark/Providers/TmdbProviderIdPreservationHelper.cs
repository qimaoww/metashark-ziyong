// <copyright file="TmdbProviderIdPreservationHelper.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Model.Entities;

    public static class TmdbProviderIdPreservationHelper
    {
        public static void PreserveMovieTmdbId(string? originalTmdbId, Movie? item, bool hasVerifiedCorrection)
        {
            PreserveTmdbId(originalTmdbId, item, hasVerifiedCorrection);
        }

        public static void PreserveSeriesTmdbId(string? originalTmdbId, Series? item, bool hasVerifiedCorrection)
        {
            PreserveTmdbId(originalTmdbId, item, hasVerifiedCorrection);
        }

        private static void PreserveTmdbId(string? originalTmdbId, IHasProviderIds? item, bool hasVerifiedCorrection)
        {
            if (hasVerifiedCorrection || item == null || string.IsNullOrWhiteSpace(originalTmdbId))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(item.GetProviderId(MetadataProvider.Tmdb)))
            {
                return;
            }

            item.SetProviderId(MetadataProvider.Tmdb, originalTmdbId.Trim());
        }
    }
}
