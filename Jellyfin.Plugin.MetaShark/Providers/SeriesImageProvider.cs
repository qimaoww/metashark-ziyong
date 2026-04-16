// <copyright file="SeriesImageProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    public class SeriesImageProvider : BaseProvider, IRemoteImageProvider
    {
        public SeriesImageProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeriesImageProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
        }

        /// <inheritdoc />
        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public bool Supports(BaseItem item) => item is Series;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new List<ImageType>
        {
            ImageType.Primary,
            ImageType.Backdrop,
            ImageType.Logo,
        };

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            var sid = item.GetProviderId(DoubanProviderId);
            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            var metaSource = item.GetMetaSource(MetaSharkPlugin.ProviderId);
            var doubanAllowed = IsDoubanAllowed(this.ResolveImageSemantic());
            this.Log($"GetImages for item: {item.Name} lang: {item.GetPreferredMetadataLanguage()} [metaSource]: {metaSource}");
            if (doubanAllowed && metaSource != MetaSource.Tmdb && !string.IsNullOrEmpty(sid))
            {
                var primary = await this.DoubanApi.GetMovieAsync(sid, cancellationToken).ConfigureAwait(false);
                if (primary == null || string.IsNullOrEmpty(primary.Img))
                {
                    if (string.IsNullOrEmpty(tmdbId))
                    {
                        return Enumerable.Empty<RemoteImageInfo>();
                    }

                    metaSource = MetaSource.Tmdb;
                }
                else
                {
                    var res = new List<RemoteImageInfo>
                    {
                        new RemoteImageInfo
                        {
                            ProviderName = this.Name,
                            Url = this.GetDoubanPoster(primary),
                            Type = ImageType.Primary,
                            Language = item.GetPreferredMetadataLanguage(),
                        },
                    };

                    var backdropImgs = await this.GetBackdrop(item, primary.PrimaryLanguageCode, doubanAllowed, cancellationToken).ConfigureAwait(false);
                    var logoImgs = await this.GetLogos(item, primary.PrimaryLanguageCode, cancellationToken).ConfigureAwait(false);
                    res.AddRange(backdropImgs);
                    res.AddRange(logoImgs);
                    return res;
                }
            }

            if (!string.IsNullOrEmpty(tmdbId) && (metaSource == MetaSource.Tmdb || !doubanAllowed))
            {
                var language = item.GetPreferredMetadataLanguage();

                var movie = await this.TmdbApi
                .GetSeriesAsync(tmdbId.ToInt(), language, language, cancellationToken)
                .ConfigureAwait(false);

                // 设定language会导致图片被过滤，这里设为null，保持取全部语言图片
                var images = await this.TmdbApi
                .GetSeriesImagesAsync(tmdbId.ToInt(), string.Empty, string.Empty, cancellationToken)
                .ConfigureAwait(false);

                if (movie == null || images == null)
                {
                    return Enumerable.Empty<RemoteImageInfo>();
                }

                var remoteImages = new List<RemoteImageInfo>();

                remoteImages.AddRange(images.Posters.Where(x => x.FilePath == movie.PosterPath).Select(x => new RemoteImageInfo
                {
                    ProviderName = this.Name,
                    Url = this.TmdbApi.GetPosterUrl(x.FilePath)?.ToString(),
                    Type = ImageType.Primary,
                    CommunityRating = x.VoteAverage,
                    VoteCount = x.VoteCount,
                    Width = x.Width,
                    Height = x.Height,
                    Language = language,
                    RatingType = RatingType.Score,
                }));

                remoteImages.AddRange(images.Backdrops.Where(x => x.FilePath == movie.BackdropPath).Select(x => new RemoteImageInfo
                {
                    ProviderName = this.Name,
                    Url = this.TmdbApi.GetBackdropUrl(x.FilePath)?.ToString(),
                    Type = ImageType.Backdrop,
                    CommunityRating = x.VoteAverage,
                    VoteCount = x.VoteCount,
                    Width = x.Width,
                    Height = x.Height,
                    Language = language,
                    RatingType = RatingType.Score,
                }));

                remoteImages.AddRange(images.Logos.Select(x => new RemoteImageInfo
                {
                    ProviderName = this.Name,
                    Url = this.TmdbApi.GetLogoUrl(x.FilePath)?.ToString(),
                    Type = ImageType.Logo,
                    CommunityRating = x.VoteAverage,
                    VoteCount = x.VoteCount,
                    Width = x.Width,
                    Height = x.Height,
                    Language = AdjustImageLanguage(x.Iso_639_1, language),
                    RatingType = RatingType.Score,
                }));

                // TODO：jellyfin 内部判断取哪个图片时，还会默认使用 OrderByLanguageDescending 排序一次，这里排序没用
                return remoteImages.OrderByLanguageDescending(language);
            }

            this.Log($"Got images failed because the images of \"{item.Name}\" is empty!");
            return new List<RemoteImageInfo>();
        }

        /// <summary>
        /// Query for a background photo.
        /// </summary>
        /// <param name="cancellationToken">Instance of the <see cref="CancellationToken"/> interface.</param>
        private async Task<IEnumerable<RemoteImageInfo>> GetBackdrop(BaseItem item, string alternativeImageLanguage, bool doubanAllowed, CancellationToken cancellationToken)
        {
            var sid = item.GetProviderId(DoubanProviderId);
            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            var list = new List<RemoteImageInfo>();

            // 从豆瓣获取背景图
            if (doubanAllowed && !string.IsNullOrEmpty(sid))
            {
                var photo = await this.DoubanApi.GetWallpaperBySidAsync(sid, cancellationToken).ConfigureAwait(false);
                if (photo != null && photo.Count > 0)
                {
                    this.Log("GetBackdrop from douban sid: {0}", sid);
                    list = photo.Where(x => x.Width >= 1280 && x.Width <= 4096 && x.Width > x.Height * 1.3).Select(x =>
                    {
                        if (Config.EnableDoubanBackdropRaw)
                        {
                            return new RemoteImageInfo
                            {
                                ProviderName = this.Name,
                                Url = this.GetProxyImageUrl(new Uri(x.Raw, UriKind.Absolute)).ToString(),
                                Height = x.Height,
                                Width = x.Width,
                                Type = ImageType.Backdrop,
                                Language = "zh",
                            };
                        }
                        else
                        {
                            return new RemoteImageInfo
                            {
                                ProviderName = this.Name,
                                Url = this.GetProxyImageUrl(new Uri(x.Large, UriKind.Absolute)).ToString(),
                                Type = ImageType.Backdrop,
                                Language = "zh",
                            };
                        }
                    }).ToList();
                }
            }

            // 添加 TheMovieDb 背景图为备选
            if (Config.EnableTmdbBackdrop && !string.IsNullOrEmpty(tmdbId))
            {
                var language = item.GetPreferredMetadataLanguage();
                var movie = await this.TmdbApi
                .GetSeriesAsync(tmdbId.ToInt(), language, language, cancellationToken)
                .ConfigureAwait(false);

                if (movie != null && !string.IsNullOrEmpty(movie.BackdropPath))
                {
                    this.Log("GetBackdrop from tmdb id: {0} lang: {1}", tmdbId, language);
                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = this.Name,
                        Url = this.TmdbApi.GetBackdropUrl(movie.BackdropPath)?.ToString(),
                        Type = ImageType.Backdrop,
                        Language = language,
                    });
                }
            }

            return list;
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetLogos(BaseItem item, string alternativeImageLanguage, CancellationToken cancellationToken)
        {
            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            var language = item.GetPreferredMetadataLanguage();
            var list = new List<RemoteImageInfo>();
            if (Config.EnableTmdbLogo && !string.IsNullOrEmpty(tmdbId))
            {
                this.Log("GetLogos from tmdb id: {0}", tmdbId);
                var images = await this.TmdbApi
                .GetSeriesImagesAsync(tmdbId.ToInt(), string.Empty, string.Empty, cancellationToken)
                .ConfigureAwait(false);

                if (images != null)
                {
                    list.AddRange(images.Logos.Select(x => new RemoteImageInfo
                    {
                        ProviderName = this.Name,
                        Url = this.TmdbApi.GetLogoUrl(x.FilePath)?.ToString(),
                        Type = ImageType.Logo,
                        CommunityRating = x.VoteAverage,
                        VoteCount = x.VoteCount,
                        Width = x.Width,
                        Height = x.Height,
                        Language = AdjustImageLanguage(x.Iso_639_1, language),
                        RatingType = RatingType.Score,
                    }));
                }
            }

            // TODO：jellyfin 内部判断取哪个图片时，还会默认使用 OrderByLanguageDescending 排序一次，这里排序没用
            //       默认图片优先级是：默认语言 > 无语言 > en > 其他语言
            return AdjustImageLanguagePriority(list, language, alternativeImageLanguage);
        }
    }
}
