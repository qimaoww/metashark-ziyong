// <copyright file="SeasonImageProvider.cs" company="PlaceholderCompany">
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
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    public class SeasonImageProvider : BaseProvider, IRemoteImageProvider
    {
        public SeasonImageProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeasonImageProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
        }

        /// <inheritdoc />
        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public bool Supports(BaseItem item) => item is Season;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            this.Log("开始获取季图片. name: {0} seasonNumber: {1}", item.Name, item.IndexNumber);
            var season = (Season)item;
            var series = season.Series;
            var metaSource = series?.GetMetaSource(MetaSharkPlugin.ProviderId) ?? MetaSource.None;
            var imageSemantic = this.ResolveImageSemantic();
            var doubanAllowed = IsDoubanAllowed(imageSemantic);
            var allowManualDoubanForSeasonImage = this.ShouldAllowDoubanForManualSeasonImageRequest(season, metaSource, imageSemantic);

            // get image from douban
            var sid = item.GetProviderId(DoubanProviderId);
            var shouldUseDoubanImage = !string.IsNullOrEmpty(sid)
                && (allowManualDoubanForSeasonImage
                    || (doubanAllowed && metaSource != MetaSource.Tmdb));
            if (shouldUseDoubanImage)
            {
                var primary = await this.DoubanApi.GetMovieAsync(sid!, cancellationToken).ConfigureAwait(false);
                if (primary != null && !string.IsNullOrEmpty(primary.Img))
                {
                    var res = new List<RemoteImageInfo>
                    {
                        new RemoteImageInfo
                        {
                            ProviderName = primary.Name,
                            Url = this.GetDoubanPoster(primary),
                            Type = ImageType.Primary,
                            Language = "zh",
                        },
                    };
                    return res;
                }
            }

            // get image form TMDB
            var seriesTmdbId = Convert.ToInt32(series?.GetProviderId(MetadataProvider.Tmdb), CultureInfo.InvariantCulture);
            if (seriesTmdbId <= 0 || season?.IndexNumber == null)
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var language = item.GetPreferredMetadataLanguage();
            var seasonResult = await this.TmdbApi
                .GetSeasonAsync(seriesTmdbId, season.IndexNumber.Value, string.Empty, string.Empty, cancellationToken)
                .ConfigureAwait(false);
            var posters = seasonResult?.Images?.Posters;
            if (posters == null)
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var remoteImages = new RemoteImageInfo[posters.Count];
            for (var i = 0; i < posters.Count; i++)
            {
                var image = posters[i];
                remoteImages[i] = new RemoteImageInfo
                {
                    Url = this.TmdbApi.GetPosterUrl(image.FilePath)?.ToString(),
                    CommunityRating = image.VoteAverage,
                    VoteCount = image.VoteCount,
                    Width = image.Width,
                    Height = image.Height,
                    Language = AdjustImageLanguage(image.Iso_639_1, language),
                    ProviderName = this.Name,
                    Type = ImageType.Primary,
                };
            }

            return remoteImages.OrderByLanguageDescending(language);
        }

        private bool ShouldAllowDoubanForManualSeasonImageRequest(Season season, MetaSource metaSource, DefaultScraperSemantic imageSemantic)
        {
            ArgumentNullException.ThrowIfNull(season);

            if (string.IsNullOrEmpty(season.GetProviderId(DoubanProviderId)))
            {
                return false;
            }

            if (imageSemantic == DefaultScraperSemantic.ManualSearch)
            {
                return true;
            }

            return this.IsManualMatchRequest() && metaSource == MetaSource.Douban;
        }
    }
}
