// <copyright file="EpisodeImageProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
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
            this.Log("开始获取单集图片. name: {0} episodeNumber: {1} seasonNumber: {2}", item.Name, item.IndexNumber, item.ParentIndexNumber);

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
            if (this.TryResolvePersistedSeriesTmdbCorrection(series?.GetProviderId(DoubanProviderId), seriesTmdbIdText, out var correctedSeriesTmdbId))
            {
                seriesTmdbIdText = correctedSeriesTmdbId;
            }

            var parsedTmdbId = int.TryParse(seriesTmdbIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seriesTmdbId);

            if (!parsedTmdbId || seriesTmdbId <= 0)
            {
                this.outcomeReporter.ReportHardMiss(item, "MissingSeriesTmdbId");
                this.Log("获取单集图片失败，seriesTmdbId 为空");
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var seasonNumber = episode.ParentIndexNumber;
            var episodeNumber = episode.IndexNumber;

            if (seasonNumber is null || episodeNumber is null or 0)
            {
                this.outcomeReporter.ReportHardMiss(item, "InvalidEpisodeNumber");
                this.Log("获取单集图片失败，seasonNumber 或 episodeNumber 为空. seasonNumber: {0} episodeNumber: {1}", seasonNumber, episodeNumber);
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var language = item.GetPreferredMetadataLanguage();
            var displayOrder = series?.DisplayOrder ?? string.Empty;

            var episodeResult = await this.GetEpisodeAsync(seriesTmdbId, seasonNumber, episodeNumber, displayOrder, language, language, cancellationToken)
                .ConfigureAwait(false);
            if (episodeResult == null)
            {
                var fallbackEpisodeNumber = this.TryResolveEpisodeGroupNumberFromPath(episode, seasonNumber.Value, episodeNumber.Value);
                if (fallbackEpisodeNumber != null)
                {
                    this.Log(
                        "TMDb 单集图片按当前编号未命中，尝试按文件名剧集组编号回退. seriesTmdbId: {0} seasonNumber: {1} episodeNumber: {2} fallbackSeasonNumber: {3} fallbackEpisodeNumber: {4} displayOrder: {5}",
                        seriesTmdbId,
                        seasonNumber,
                        episodeNumber,
                        fallbackEpisodeNumber.Value.SeasonNumber,
                        fallbackEpisodeNumber.Value.EpisodeNumber,
                        displayOrder);
                    episodeResult = await this.GetEpisodeAsync(
                            seriesTmdbId,
                            fallbackEpisodeNumber.Value.SeasonNumber,
                            fallbackEpisodeNumber.Value.EpisodeNumber,
                            displayOrder,
                            language,
                            language,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (episodeResult == null)
                {
                    this.outcomeReporter.ReportHardMiss(item, "EpisodeNotFound");
                    this.Log("未找到 TMDb 单集图片数据. seriesTmdbId: {0} seasonNumber: {1} episodeNumber: {2} displayOrder: {3}", seriesTmdbId, seasonNumber, episodeNumber, displayOrder);
                    return Enumerable.Empty<RemoteImageInfo>();
                }
            }

            var stillPath = episodeResult.StillPath;
            if (string.IsNullOrEmpty(stillPath))
            {
                stillPath = episodeResult.Images?.Stills?
                    .FirstOrDefault(still => !string.IsNullOrEmpty(still.FilePath))?
                    .FilePath;
            }

            var result = new List<RemoteImageInfo>();
            if (!string.IsNullOrEmpty(stillPath))
            {
                result.Add(new RemoteImageInfo
                {
                    Url = this.TmdbApi.GetStillUrl(stillPath)?.ToString(),
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

        private (int SeasonNumber, int EpisodeNumber)? TryResolveEpisodeGroupNumberFromPath(Episode episode, int currentSeasonNumber, int currentEpisodeNumber)
        {
            var path = episode.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var fileName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var parseResult = NameParser.ParseEpisode(fileName);
            var fallbackEpisodeNumber = parseResult.IndexNumber;
            if (fallbackEpisodeNumber is null or <= 0)
            {
                return null;
            }

            var fallbackSeasonNumber = parseResult.ParentIndexNumber;
            if (!fallbackSeasonNumber.HasValue)
            {
                var seasonPath = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(seasonPath))
                {
                    fallbackSeasonNumber = this.LibraryManager.GetSeasonNumberFromPath(seasonPath, episode.ParentId)
                        ?? this.GuessSeasonNumberByDirectoryName(seasonPath);
                }
            }

            if (fallbackSeasonNumber is null)
            {
                return null;
            }

            if (fallbackSeasonNumber.Value == currentSeasonNumber
                && fallbackEpisodeNumber.Value == currentEpisodeNumber)
            {
                return null;
            }

            this.Log(
                "已从文件路径解析单集图片回退编号. path: {0} seasonNumber: {1} episodeNumber: {2}",
                path,
                fallbackSeasonNumber.Value,
                fallbackEpisodeNumber.Value);
            return (fallbackSeasonNumber.Value, fallbackEpisodeNumber.Value);
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
