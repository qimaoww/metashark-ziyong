// <copyright file="EpisodeImageProvider.cs" company="PlaceholderCompany">
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
    using Jellyfin.Plugin.MetaShark.Workers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    public class EpisodeImageProvider : BaseProvider, IRemoteImageProvider
    {
        private readonly ITvImageRefillOutcomeReporter outcomeReporter;

        public EpisodeImageProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi, ITvImageRefillOutcomeReporter? outcomeReporter = null)
            : base(httpClientFactory, loggerFactory.CreateLogger<EpisodeImageProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
            this.outcomeReporter = outcomeReporter ?? new NullTvImageRefillOutcomeReporter();
        }

        /// <inheritdoc />
        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public bool Supports(BaseItem item) => item is Episode;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            this.Log($"GetEpisodeImages of [name]: {item.Name} number: {item.IndexNumber} ParentIndexNumber: {item.ParentIndexNumber}");

            var episode = (MediaBrowser.Controller.Entities.TV.Episode)item;
            MediaBrowser.Controller.Entities.TV.Series? series;
            try
            {
                series = episode.Series;
            }
            catch (NullReferenceException)
            {
                series = null;
            }

            var seriesTmdbIdText = series?.GetProviderId(MetadataProvider.Tmdb);
            if (string.IsNullOrWhiteSpace(seriesTmdbIdText))
            {
                seriesTmdbIdText = item.GetProviderId(MetadataProvider.Tmdb);
            }

            var parsedTmdbId = int.TryParse(seriesTmdbIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seriesTmdbId);

            if (!parsedTmdbId || seriesTmdbId <= 0)
            {
                this.outcomeReporter.ReportHardMiss(item, "MissingSeriesTmdbId");
                this.Log($"[GetEpisodeImages] The seriesTmdbId is empty!");
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var seasonNumber = episode.ParentIndexNumber;
            var episodeNumber = episode.IndexNumber;

            if (seasonNumber is null || episodeNumber is null or 0)
            {
                this.outcomeReporter.ReportHardMiss(item, "InvalidEpisodeNumber");
                this.Log($"[GetEpisodeImages] The seasonNumber or episodeNumber is empty! seasonNumber: {seasonNumber} episodeNumber: {episodeNumber}");
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var language = item.GetPreferredMetadataLanguage();
            var displayOrder = series?.DisplayOrder ?? string.Empty;

            var episodeResult = await this.GetEpisodeAsync(seriesTmdbId, seasonNumber, episodeNumber, displayOrder, language, language, cancellationToken)
                .ConfigureAwait(false);
            if (episodeResult == null)
            {
                this.outcomeReporter.ReportHardMiss(item, "EpisodeNotFound");
                this.Log("GetEpisodeImages] 找不到tmdb剧集数据. seriesTmdbId: {0} seasonNumber: {1} episodeNumber: {2} displayOrder: {3}", seriesTmdbId, seasonNumber, episodeNumber, displayOrder);
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var result = new List<RemoteImageInfo>();
            if (!string.IsNullOrEmpty(episodeResult.StillPath))
            {
                result.Add(new RemoteImageInfo
                {
                    Url = this.TmdbApi.GetStillUrl(episodeResult.StillPath)?.ToString(),
                    CommunityRating = episodeResult.VoteAverage,
                    VoteCount = episodeResult.VoteCount,
                    ProviderName = this.Name,
                    Type = ImageType.Primary,
                });
            }

            if (result.Count > 0)
            {
                this.outcomeReporter.ReportSuccess(item);
            }
            else
            {
                this.outcomeReporter.ReportHardMiss(item, "NoStillPath");
            }

            return result;
        }

        private sealed class NullTvImageRefillOutcomeReporter : ITvImageRefillOutcomeReporter
        {
            public void ReportHardMiss(BaseItem item, string reason)
            {
            }

            public void ReportSuccess(BaseItem item)
            {
            }

            public void ReportTransientFailure(BaseItem item, string reason)
            {
            }
        }
    }
}
