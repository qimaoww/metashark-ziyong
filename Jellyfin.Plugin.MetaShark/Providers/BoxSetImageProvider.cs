// <copyright file="BoxSetImageProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Extensions;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// BoxSet image provider powered by TMDb.
    /// </summary>
    public class BoxSetImageProvider : BaseProvider, IRemoteImageProvider
    {
        public BoxSetImageProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<BoxSetImageProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
        }

        /// <inheritdoc />
        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public bool Supports(BaseItem item)
        {
            return item is BoxSet;
        }

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item) =>
        [
            ImageType.Primary,
            ImageType.Backdrop,
            ImageType.Thumb
        ];

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            var tmdbId = Convert.ToInt32(item.GetProviderId(MetadataProvider.Tmdb), CultureInfo.InvariantCulture);
            this.Log("开始获取合集图片. name: {0} tmdbId: {1}", item.Name, tmdbId);

            if (tmdbId <= 0)
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var language = item.GetPreferredMetadataLanguage();
            var isManualImageRequest = this.ResolveImageSemantic() == DefaultScraperSemantic.ManualSearch;

            // TODO use image languages if All Languages isn't toggled, but there's currently no way to get that value in here
            var collection = await this.TmdbApi
                .GetCollectionAsync(tmdbId, string.Empty, string.Empty, cancellationToken)
                .ConfigureAwait(false);

            if (collection?.Images is null)
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var posters = collection.Images.Posters;
            var backdrops = collection.Images.Backdrops;
            var remoteImages = new List<RemoteImageInfo>(posters.Count + backdrops.Count);
            remoteImages.AddRange(posters.Select(x => new RemoteImageInfo
            {
                ProviderName = this.Name,
                Url = this.TmdbApi.GetPosterUrl(x.FilePath)?.ToString(),
                Type = ImageType.Primary,
                CommunityRating = x.VoteAverage,
                VoteCount = x.VoteCount,
                Width = x.Width,
                Height = x.Height,
                Language = isManualImageRequest ? x.Iso_639_1 : AdjustImageLanguage(x.Iso_639_1, language),
                RatingType = RatingType.Score,
            }));

            remoteImages.AddRange(backdrops.Select(x => new RemoteImageInfo
            {
                ProviderName = this.Name,
                Url = this.TmdbApi.GetBackdropUrl(x.FilePath)?.ToString(),
                Type = ImageType.Backdrop,
                CommunityRating = x.VoteAverage,
                VoteCount = x.VoteCount,
                Width = x.Width,
                Height = x.Height,
                Language = isManualImageRequest ? x.Iso_639_1 : AdjustImageLanguage(x.Iso_639_1, language),
                RatingType = RatingType.Score,
            }));

            return isManualImageRequest
                ? remoteImages.FilterManualRemoteImagesByLanguage()
                : remoteImages.OrderByLanguageDescending(language);
        }
    }
}
