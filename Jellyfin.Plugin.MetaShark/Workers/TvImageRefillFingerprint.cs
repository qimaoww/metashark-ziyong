// <copyright file="TvImageRefillFingerprint.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Globalization;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Model.Entities;

    public static class TvImageRefillFingerprint
    {
        public static string Create(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            var path = item.Path ?? string.Empty;

            return item switch
            {
                Episode episode => string.Join(
                    "|",
                    path,
                    ResolveOfficialTmdbId(episode.Series),
                    episode.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    episode.IndexNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                Season season => string.Join(
                    "|",
                    path,
                    ResolveOfficialTmdbId(season.Series),
                    season.IndexNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                Series series => string.Join(
                    "|",
                    path,
                    ResolveOfficialTmdbId(series)),
                _ => path,
            };
        }

        private static string ResolveOfficialTmdbId(BaseItem? item)
        {
            return item?.GetProviderId(MetadataProvider.Tmdb) ?? string.Empty;
        }
    }
}
