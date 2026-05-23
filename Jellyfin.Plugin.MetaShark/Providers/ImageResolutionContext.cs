// <copyright file="ImageResolutionContext.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;

    internal sealed class ImageResolutionContext
    {
        private ImageResolutionContext(
            string? doubanId,
            string? tmdbId,
            string preferredLanguage,
            MetaSource metaSource,
            DefaultScraperSemantic semantic,
            bool isManualImageRequest,
            bool doubanAllowed)
        {
            this.DoubanId = doubanId;
            this.TmdbId = tmdbId;
            this.PreferredLanguage = preferredLanguage;
            this.MetaSource = metaSource;
            this.Semantic = semantic;
            this.IsManualImageRequest = isManualImageRequest;
            this.DoubanAllowed = doubanAllowed;
        }

        public string? DoubanId { get; }

        public string? TmdbId { get; private set; }

        public string PreferredLanguage { get; }

        public MetaSource MetaSource { get; private set; }

        public DefaultScraperSemantic Semantic { get; }

        public bool IsManualImageRequest { get; }

        public bool DoubanAllowed { get; }

        public static ImageResolutionContext FromItem(BaseItem item, DefaultScraperSemantic semantic, bool doubanAllowed)
        {
            ArgumentNullException.ThrowIfNull(item);

            return new ImageResolutionContext(
                item.GetProviderId(BaseProvider.DoubanProviderId),
                item.GetProviderId(MetadataProvider.Tmdb),
                item.GetPreferredMetadataLanguage(),
                item.GetMetaSource(MetaSharkPlugin.ProviderId),
                semantic,
                semantic == DefaultScraperSemantic.ManualSearch,
                doubanAllowed);
        }

        public void ApplySeriesTmdbCorrection(string correctedTmdbId)
        {
            this.TmdbId = correctedTmdbId;
            this.MetaSource = MetaSource.Tmdb;
        }
    }
}
