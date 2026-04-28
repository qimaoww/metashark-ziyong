// <copyright file="TvImageRefillFingerprint.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Globalization;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;

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
                    episode.Series?.GetTmdbId() ?? string.Empty,
                    episode.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    episode.IndexNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                Season season => string.Join(
                    "|",
                    path,
                    season.Series?.GetTmdbId() ?? string.Empty,
                    season.IndexNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                Series series => string.Join(
                    "|",
                    path,
                    series.GetTmdbId() ?? string.Empty),
                _ => path,
            };
        }
    }
}
