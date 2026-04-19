// <copyright file="PersonImageProvider.cs" company="PlaceholderCompany">
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
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    public class PersonImageProvider : BaseProvider, IRemoteImageProvider
    {
        public PersonImageProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<PersonImageProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
        }

        /// <inheritdoc />
        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public bool Supports(BaseItem item) => item is Person;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            var list = new List<RemoteImageInfo>();
            var cid = item.GetProviderId(DoubanProviderId);
            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            var metaSource = item.GetMetaSource(MetaSharkPlugin.ProviderId);
            var language = item.GetPreferredMetadataLanguage();
            var doubanAllowed = IsDoubanAllowed(this.ResolveImageSemantic());
            var usedDouban = false;
            this.Log("开始获取人物图片. name: {0} metaSource: {1}", item.Name, metaSource);
            if (doubanAllowed && !string.IsNullOrEmpty(cid))
            {
                var celebrity = await this.DoubanApi.GetCelebrityAsync(cid, cancellationToken).ConfigureAwait(false);
                if (celebrity != null)
                {
                    usedDouban = true;
                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = this.Name,
                        Url = this.GetProxyImageUrl(new Uri(celebrity.Img, UriKind.Absolute)).ToString(),
                        Type = ImageType.Primary,
                        Language = "zh",
                    });
                }
            }

            if (usedDouban && !string.IsNullOrEmpty(cid))
            {
                var photos = await this.DoubanApi.GetCelebrityPhotosAsync(cid, cancellationToken).ConfigureAwait(false);
                photos.ForEach(x =>
                {
                    // 过滤不是竖图
                    if (x.Width < 400 || x.Height < x.Width * 1.3)
                    {
                        return;
                    }

                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = this.Name,
                        Url = this.GetProxyImageUrl(new Uri(x.Raw, UriKind.Absolute)).ToString(),
                        Width = x.Width,
                        Height = x.Height,
                        Type = ImageType.Primary,
                        Language = "zh",
                    });
                });
            }

            if (list.Count == 0 && !string.IsNullOrEmpty(tmdbId))
            {
                var person = await this.TmdbApi.GetPersonAsync(tmdbId.ToInt(), cancellationToken).ConfigureAwait(false);
                var profiles = person?.Images?.Profiles;
                if (profiles != null)
                {
                    list.AddRange(profiles.Select(x => new RemoteImageInfo
                    {
                        ProviderName = this.Name,
                        Url = this.TmdbApi.GetProfileUrl(x.FilePath)?.ToString(),
                        Width = x.Width,
                        Height = x.Height,
                        Type = ImageType.Primary,
                        Language = AdjustImageLanguage(x.Iso_639_1, language),
                        CommunityRating = x.VoteAverage,
                        VoteCount = x.VoteCount,
                        RatingType = RatingType.Score,
                    }).Where(x => !string.IsNullOrEmpty(x.Url)));
                }
            }

            if (list.Count == 0)
            {
                this.Log("获取人物图片失败，没有可用图片. name: {0}", item.Name);
            }

            return list.OrderByLanguageDescending(language);
        }
    }
}
