// <copyright file="TmdbApi.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Client;
    using TMDbLib.Objects.Collections;
    using TMDbLib.Objects.Find;
    using TMDbLib.Objects.General;
    using TMDbLib.Objects.Movies;
    using TMDbLib.Objects.People;
    using TMDbLib.Objects.Search;
    using TMDbLib.Objects.TvShows;

    public class TmdbApi : IDisposable
    {
        private static readonly int CacheDurationInHours = int.Parse("1", CultureInfo.InvariantCulture);
        private static readonly string DefaultApiKey = string.Concat("4219e299c89411838049ab0dab19ebd5");
        private static readonly string DefaultApiHost = string.Concat("api.tmdb.org");
        private static readonly JsonSerializerOptions EpisodePlacementJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly ILogger<TmdbApi> logger;
        private readonly MemoryCache memoryCache;
        private readonly TMDbClient tmDbClient;
        private readonly Action<ILogger, string, Exception?> logTmdbError;
        private readonly string apiKey;
        private readonly string apiHost;

        /// <summary>
        /// Initializes a new instance of the <see cref="TmdbApi"/> class.
        /// </summary>
        public TmdbApi(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<TmdbApi>();
            this.memoryCache = new MemoryCache(new MemoryCacheOptions());
            this.logTmdbError = LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, nameof(TmdbApi)), "TMDb request failed in {Operation}");
            var config = MetaSharkPlugin.Instance?.Configuration;
            this.apiKey = string.IsNullOrEmpty(config?.TmdbApiKey) ? DefaultApiKey : config.TmdbApiKey;
            this.apiHost = string.IsNullOrEmpty(config?.TmdbHost) ? DefaultApiHost : config.TmdbHost;
            this.tmDbClient = new TMDbClient(this.apiKey, true, this.apiHost, null, config?.GetTmdbWebProxy());
            this.tmDbClient.Timeout = TimeSpan.FromSeconds(10);

            // Not really interested in NotFoundException
            this.tmDbClient.ThrowApiExceptions = false;
        }

        /// <summary>
        /// Gets a movie from the TMDb API based on its TMDb id.
        /// </summary>
        /// <param name="tmdbId">The movie's TMDb id.</param>
        /// <param name="language">The movie's language.</param>
        /// <param name="imageLanguages">A comma-separated list of image languages.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb movie or null if not found.</returns>
        public async Task<Movie?> GetMovieAsync(int tmdbId, string language, string imageLanguages, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return null;
            }

            var key = $"movie-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{imageLanguages}";
            if (this.memoryCache.TryGetValue(key, out Movie? movie))
            {
                return movie;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                movie = await this.tmDbClient.GetMovieAsync(
                    tmdbId,
                    NormalizeLanguage(language),
                    GetImageLanguagesParam(imageLanguages),
                    MovieMethods.Credits | MovieMethods.Releases | MovieMethods.Images | MovieMethods.Keywords | MovieMethods.Videos,
                    cancellationToken).ConfigureAwait(false);

                if (movie != null)
                {
                    this.memoryCache.Set(key, movie, TimeSpan.FromHours(CacheDurationInHours));
                }

                return movie;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.GetMovieAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.GetMovieAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Gets a movie images from the TMDb API based on its TMDb id.
        /// </summary>
        /// <param name="tmdbId">The movie's TMDb id.</param>
        /// <param name="language">The movie's language.</param>
        /// <param name="imageLanguages">A comma-separated list of image languages.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb movie images or null if not found.</returns>
        public async Task<ImagesWithId?> GetMovieImagesAsync(int tmdbId, string language, string imageLanguages, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return null;
            }

            var key = $"movie-images-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{imageLanguages}";
            if (this.memoryCache.TryGetValue(key, out ImagesWithId? images))
            {
                return images;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                images = await this.tmDbClient.GetMovieImagesAsync(
                    tmdbId,
                    NormalizeLanguage(language),
                    GetImageLanguagesParam(imageLanguages),
                    cancellationToken).ConfigureAwait(false);

                if (images != null)
                {
                    this.memoryCache.Set(key, images, TimeSpan.FromHours(CacheDurationInHours));
                }

                return images;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.GetMovieImagesAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.GetMovieImagesAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Gets a collection from the TMDb API based on its TMDb id.
        /// </summary>
        /// <param name="tmdbId">The collection's TMDb id.</param>
        /// <param name="language">The collection's language.</param>
        /// <param name="imageLanguages">A comma-separated list of image languages.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb collection or null if not found.</returns>
        public async Task<Collection?> GetCollectionAsync(int tmdbId, string language, string imageLanguages, CancellationToken cancellationToken)
        {
            var key = $"collection-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{imageLanguages}";
            if (this.memoryCache.TryGetValue(key, out Collection? collection))
            {
                return collection;
            }

            await this.EnsureClientConfigAsync().ConfigureAwait(false);

            collection = await this.tmDbClient.GetCollectionAsync(
                tmdbId,
                NormalizeLanguage(language),
                GetImageLanguagesParam(imageLanguages),
                CollectionMethods.Images,
                cancellationToken).ConfigureAwait(false);

            if (collection != null)
            {
                this.memoryCache.Set(key, collection, TimeSpan.FromHours(CacheDurationInHours));
            }

            return collection;
        }

        /// <summary>
        /// Gets a tv show from the TMDb API based on its TMDb id.
        /// </summary>
        /// <param name="tmdbId">The tv show's TMDb id.</param>
        /// <param name="language">The tv show's language.</param>
        /// <param name="imageLanguages">A comma-separated list of image languages.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb tv show information or null if not found.</returns>
        public async Task<TvShow?> GetSeriesAsync(int tmdbId, string language, string imageLanguages, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return null;
            }

            var key = $"series-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{imageLanguages}";
            if (this.memoryCache.TryGetValue(key, out TvShow? series))
            {
                return series;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                series = await this.tmDbClient.GetTvShowAsync(
                    tmdbId,
                    language: NormalizeLanguage(language),
                    includeImageLanguage: GetImageLanguagesParam(imageLanguages),
                    extraMethods: TvShowMethods.Credits | TvShowMethods.Images | TvShowMethods.Keywords | TvShowMethods.ExternalIds | TvShowMethods.Videos | TvShowMethods.ContentRatings | TvShowMethods.EpisodeGroups,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (series != null)
                {
                    this.memoryCache.Set(key, series, TimeSpan.FromHours(CacheDurationInHours));
                }

                return series;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.GetSeriesAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.GetSeriesAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Gets a tv show images from the TMDb API based on its TMDb id.
        /// </summary>
        /// <param name="tmdbId">The tv show's TMDb id.</param>
        /// <param name="language">The tv show's language.</param>
        /// <param name="imageLanguages">A comma-separated list of image languages.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb tv show images or null if not found.</returns>
        public async Task<ImagesWithId?> GetSeriesImagesAsync(int tmdbId, string language, string imageLanguages, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return null;
            }

            var key = $"series-images-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{imageLanguages}";
            if (this.memoryCache.TryGetValue(key, out ImagesWithId? images))
            {
                return images;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                images = await this.tmDbClient.GetTvShowImagesAsync(
                    tmdbId,
                    NormalizeLanguage(language),
                    GetImageLanguagesParam(imageLanguages),
                    cancellationToken).ConfigureAwait(false);

                if (images != null)
                {
                    this.memoryCache.Set(key, images, TimeSpan.FromHours(CacheDurationInHours));
                }

                return images;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.GetSeriesImagesAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.GetSeriesImagesAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        public async Task<TvGroupCollection?> GetSeriesGroupAsync(int tvShowId, string displayOrder, string? language, string? imageLanguages, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return null;
            }

            TvGroupType? groupType =
                string.Equals(displayOrder, "originalAirDate", StringComparison.Ordinal) ? TvGroupType.OriginalAirDate :
                string.Equals(displayOrder, "absolute", StringComparison.Ordinal) ? TvGroupType.Absolute :
                string.Equals(displayOrder, "dvd", StringComparison.Ordinal) ? TvGroupType.DVD :
                string.Equals(displayOrder, "digital", StringComparison.Ordinal) ? TvGroupType.Digital :
                string.Equals(displayOrder, "storyArc", StringComparison.Ordinal) ? TvGroupType.StoryArc :
                string.Equals(displayOrder, "production", StringComparison.Ordinal) ? TvGroupType.Production :
                string.Equals(displayOrder, "tv", StringComparison.Ordinal) ? TvGroupType.TV :
                null;

            if (groupType is null)
            {
                return null;
            }

            var key = $"group-{tvShowId.ToString(CultureInfo.InvariantCulture)}-{displayOrder}-{language}";
            if (this.memoryCache.TryGetValue(key, out TvGroupCollection? group))
            {
                return group;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                var normalizedLanguage = NormalizeLanguage(language ?? string.Empty);
                var normalizedImageLanguages = imageLanguages ?? string.Empty;

                var series = await this.GetSeriesAsync(tvShowId, normalizedLanguage, normalizedImageLanguages, cancellationToken)
                    .ConfigureAwait(false);
                var episodeGroupId = series?.EpisodeGroups.Results.Find(g => g.Type == groupType)?.Id;

                if (episodeGroupId is null)
                {
                    return null;
                }

                group = await this.tmDbClient.GetTvEpisodeGroupsAsync(
                    episodeGroupId,
                    language: normalizedLanguage,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (group is not null)
                {
                    this.memoryCache.Set(key, group, TimeSpan.FromHours(CacheDurationInHours));
                }

                return group;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.GetSeriesGroupAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.GetSeriesGroupAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        public async Task<TvGroupCollection?> GetEpisodeGroupByIdAsync(string groupId, string? language, CancellationToken cancellationToken)
        {
            if (!IsEnable() || string.IsNullOrWhiteSpace(groupId))
            {
                return null;
            }

            var key = $"group-id-{groupId}-{language}";
            if (this.memoryCache.TryGetValue(key, out TvGroupCollection? group))
            {
                return group;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                var normalizedLanguage = NormalizeLanguage(language ?? string.Empty);

                group = await this.tmDbClient.GetTvEpisodeGroupsAsync(
                    groupId,
                    language: normalizedLanguage,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (group is not null)
                {
                    this.memoryCache.Set(key, group, TimeSpan.FromHours(CacheDurationInHours));
                }

                return group;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.GetEpisodeGroupByIdAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.GetEpisodeGroupByIdAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Gets a tv season from the TMDb API based on the tv show's TMDb id.
        /// </summary>
        /// <param name="tvShowId">The tv season's TMDb id.</param>
        /// <param name="seasonNumber">The season number.</param>
        /// <param name="language">The tv season's language.</param>
        /// <param name="imageLanguages">A comma-separated list of image languages.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb tv season information or null if not found.</returns>
        public async Task<TvSeason?> GetSeasonAsync(int tvShowId, int seasonNumber, string language, string imageLanguages, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return null;
            }

            var key = $"season-{tvShowId.ToString(CultureInfo.InvariantCulture)}-s{seasonNumber.ToString(CultureInfo.InvariantCulture)}-{language}-{imageLanguages}";
            if (this.memoryCache.TryGetValue(key, out TvSeason? season))
            {
                return season;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                season = await this.tmDbClient.GetTvSeasonAsync(
                    tvShowId,
                    seasonNumber,
                    language: NormalizeLanguage(language),
                    includeImageLanguage: GetImageLanguagesParam(imageLanguages),
                    extraMethods: TvSeasonMethods.Credits | TvSeasonMethods.Images | TvSeasonMethods.ExternalIds | TvSeasonMethods.Videos,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                this.memoryCache.Set(key, season, TimeSpan.FromHours(CacheDurationInHours));
                return season;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // 可能网络有问题，缓存一下避免频繁请求
                this.memoryCache.Set(key, season, TimeSpan.FromSeconds(30));
                this.logTmdbError(this.logger, nameof(this.GetSeasonAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                // 可能网络有问题，缓存一下避免频繁请求
                this.memoryCache.Set(key, season, TimeSpan.FromSeconds(30));
                this.logTmdbError(this.logger, nameof(this.GetSeasonAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Gets a movie from the TMDb API based on the tv show's TMDb id.
        /// </summary>
        /// <param name="tvShowId">The tv show's TMDb id.</param>
        /// <param name="seasonNumber">The season number.</param>
        /// <param name="episodeNumber">The episode number.</param>
        /// <param name="language">The episode's language.</param>
        /// <param name="imageLanguages">A comma-separated list of image languages.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb tv episode information or null if not found.</returns>
        public async Task<TvEpisode?> GetEpisodeAsync(int tvShowId, int seasonNumber, int episodeNumber, string language, string imageLanguages, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return null;
            }

            var key = $"episode-{tvShowId.ToString(CultureInfo.InvariantCulture)}-s{seasonNumber.ToString(CultureInfo.InvariantCulture)}e{episodeNumber.ToString(CultureInfo.InvariantCulture)}-{language}-{imageLanguages}";
            if (this.memoryCache.TryGetValue(key, out TvEpisode? episode))
            {
                return episode;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                episode = await this.tmDbClient.GetTvEpisodeAsync(
                    tvShowId,
                    seasonNumber,
                    episodeNumber,
                    language: NormalizeLanguage(language),
                    includeImageLanguage: GetImageLanguagesParam(imageLanguages),
                    extraMethods: TvEpisodeMethods.Credits | TvEpisodeMethods.Images | TvEpisodeMethods.ExternalIds | TvEpisodeMethods.Videos,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (episode != null)
                {
                    this.memoryCache.Set(key, episode, TimeSpan.FromHours(CacheDurationInHours));
                }

                return episode;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.GetEpisodeAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.GetEpisodeAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Gets TMDb episode placement metadata for specials.
        /// </summary>
        /// <param name="tvShowId">The tv show's TMDb id.</param>
        /// <param name="seasonNumber">The season number (use 0 for specials).</param>
        /// <param name="episodeNumber">The episode number.</param>
        /// <param name="language">The episode's language.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The episode placement metadata or null if not found.</returns>
        public async Task<TmdbEpisodePlacement?> GetEpisodePlacementAsync(int tvShowId, int seasonNumber, int episodeNumber, string? language, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return null;
            }

            var normalizedLanguage = NormalizeLanguage(language ?? string.Empty);
            var key = $"episode-placement-{tvShowId.ToString(CultureInfo.InvariantCulture)}-s{seasonNumber.ToString(CultureInfo.InvariantCulture)}e{episodeNumber.ToString(CultureInfo.InvariantCulture)}-{normalizedLanguage}";
            if (this.memoryCache.TryGetValue(key, out TmdbEpisodePlacement? placement))
            {
                return placement;
            }

            try
            {
                var baseHost = this.apiHost.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? this.apiHost.TrimEnd('/')
                    : $"https://{this.apiHost.TrimEnd('/')}";
                var url = new StringBuilder()
                    .Append(baseHost)
                    .Append("/3/tv/")
                    .Append(tvShowId.ToString(CultureInfo.InvariantCulture))
                    .Append("/season/")
                    .Append(seasonNumber.ToString(CultureInfo.InvariantCulture))
                    .Append("/episode/")
                    .Append(episodeNumber.ToString(CultureInfo.InvariantCulture))
                    .Append("?api_key=")
                    .Append(Uri.EscapeDataString(this.apiKey));

                if (!string.IsNullOrWhiteSpace(normalizedLanguage))
                {
                    url.Append("&language=").Append(Uri.EscapeDataString(normalizedLanguage));
                }

                using var handler = new HttpClientHandler();
                handler.CheckCertificateRevocationList = true;
                var proxy = MetaSharkPlugin.Instance?.Configuration?.GetTmdbWebProxy();
                if (proxy != null)
                {
                    handler.Proxy = proxy;
                    handler.UseProxy = true;
                }

                using var httpClient = new HttpClient(handler, false)
                {
                    Timeout = TimeSpan.FromSeconds(10),
                };

                var requestUri = new Uri(url.ToString(), UriKind.Absolute);
                using var response = await httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    this.memoryCache.Set(key, (TmdbEpisodePlacement?)null, TimeSpan.FromSeconds(30));
                    return null;
                }

                var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var stream = responseStream;
                placement = await JsonSerializer.DeserializeAsync<TmdbEpisodePlacement>(
                    stream,
                    EpisodePlacementJsonOptions,
                    cancellationToken).ConfigureAwait(false);

                if (placement != null)
                {
                    this.memoryCache.Set(key, placement, TimeSpan.FromHours(CacheDurationInHours));
                }

                return placement;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.GetEpisodePlacementAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.GetEpisodePlacementAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Gets a person eg. cast or crew member from the TMDb API based on its TMDb id.
        /// </summary>
        /// <param name="personTmdbId">The person's TMDb id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb person information or null if not found.</returns>
        public async Task<Person?> GetPersonAsync(int personTmdbId, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return null;
            }

            var key = $"person-{personTmdbId.ToString(CultureInfo.InvariantCulture)}";
            if (this.memoryCache.TryGetValue(key, out Person? person))
            {
                return person;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                person = await this.tmDbClient.GetPersonAsync(
                    personTmdbId,
                    PersonMethods.TvCredits | PersonMethods.MovieCredits | PersonMethods.Images | PersonMethods.ExternalIds,
                    cancellationToken).ConfigureAwait(false);

                if (person != null)
                {
                    this.memoryCache.Set(key, person, TimeSpan.FromHours(CacheDurationInHours));
                }

                return person;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.GetPersonAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.GetPersonAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Gets an item from the TMDb API based on its id from an external service eg. IMDb id, TvDb id.
        /// </summary>
        /// <param name="externalId">The item's external id.</param>
        /// <param name="source">The source of the id eg. IMDb.</param>
        /// <param name="language">The item's language.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb item or null if not found.</returns>
        public async Task<FindContainer?> FindByExternalIdAsync(
            string externalId,
            FindExternalSource source,
            string language,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(externalId);
            if (!IsEnable())
            {
                return null;
            }

            var key = $"find-{source.ToString()}-{externalId.ToString(CultureInfo.InvariantCulture)}-{language}";
            if (this.memoryCache.TryGetValue(key, out FindContainer? result))
            {
                return result;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                result = await this.tmDbClient.FindAsync(
                    source,
                    externalId,
                    NormalizeLanguage(language),
                    cancellationToken).ConfigureAwait(false);

                if (result != null)
                {
                    this.memoryCache.Set(key, result, TimeSpan.FromHours(CacheDurationInHours));
                }

                return result;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.FindByExternalIdAsync), ex);
                return null;
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.FindByExternalIdAsync), ex);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Searches for a tv show using the TMDb API based on its name.
        /// </summary>
        /// <param name="name">The name of the tv show.</param>
        /// <param name="language">The tv show's language.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb tv show information.</returns>
        public async Task<IReadOnlyList<SearchTv>> SearchSeriesAsync(string name, string language, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return new List<SearchTv>();
            }

            var key = $"searchseries-{name}-{language}";
            if (this.memoryCache.TryGetValue(key, out SearchContainer<SearchTv>? series) && series != null)
            {
                return series.Results;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                var enableAdult = MetaSharkPlugin.Instance?.Configuration?.EnableTmdbAdult ?? false;
                var searchResults = await this.tmDbClient
                    .SearchTvShowAsync(name, NormalizeLanguage(language), includeAdult: enableAdult, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (searchResults.Results.Count > 0)
                {
                    this.memoryCache.Set(key, searchResults, TimeSpan.FromHours(CacheDurationInHours));
                }

                return searchResults.Results;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new List<SearchTv>();
            }
            catch (HttpRequestException)
            {
                return new List<SearchTv>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Searches for a person based on their name using the TMDb API.
        /// </summary>
        /// <param name="name">The name of the person.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb person information.</returns>
        public async Task<IReadOnlyList<SearchPerson>> SearchPersonAsync(string name, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return new List<SearchPerson>();
            }

            var key = $"searchperson-{name}";
            if (this.memoryCache.TryGetValue(key, out SearchContainer<SearchPerson>? person) && person != null)
            {
                return person.Results;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                var enableAdult = MetaSharkPlugin.Instance?.Configuration?.EnableTmdbAdult ?? false;
                var searchResults = await this.tmDbClient
                    .SearchPersonAsync(name, includeAdult: enableAdult, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (searchResults.Results.Count > 0)
                {
                    this.memoryCache.Set(key, searchResults, TimeSpan.FromHours(CacheDurationInHours));
                }

                return searchResults.Results;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.SearchPersonAsync), ex);
                return new List<SearchPerson>();
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.SearchPersonAsync), ex);
                return new List<SearchPerson>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Searches for a movie based on its name using the TMDb API.
        /// </summary>
        /// <param name="name">The name of the movie.</param>
        /// <param name="language">The movie's language.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb movie information.</returns>
        public Task<IReadOnlyList<SearchMovie>> SearchMovieAsync(string name, string language, CancellationToken cancellationToken)
        {
            return this.SearchMovieAsync(name, 0, language, cancellationToken);
        }

        /// <summary>
        /// Searches for a movie based on its name using the TMDb API.
        /// </summary>
        /// <param name="name">The name of the movie.</param>
        /// <param name="year">The release year of the movie.</param>
        /// <param name="language">The movie's language.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb movie information.</returns>
        public async Task<IReadOnlyList<SearchMovie>> SearchMovieAsync(string name, int year, string language, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return new List<SearchMovie>();
            }

            var key = $"moviesearch-{name}-{year.ToString(CultureInfo.InvariantCulture)}-{language}";
            if (this.memoryCache.TryGetValue(key, out SearchContainer<SearchMovie>? movies) && movies != null)
            {
                return movies.Results;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                var enableAdult = MetaSharkPlugin.Instance?.Configuration?.EnableTmdbAdult ?? false;
                var searchResults = await this.tmDbClient
                    .SearchMovieAsync(name, NormalizeLanguage(language), includeAdult: enableAdult, year: year, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (searchResults.Results.Count > 0)
                {
                    this.memoryCache.Set(key, searchResults, TimeSpan.FromHours(CacheDurationInHours));
                }

                return searchResults.Results;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.SearchMovieAsync), ex);
                return new List<SearchMovie>();
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.SearchMovieAsync), ex);
                return new List<SearchMovie>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Searches for a collection based on its name using the TMDb API.
        /// </summary>
        /// <param name="name">The name of the collection.</param>
        /// <param name="language">The collection's language.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The TMDb collection information.</returns>
        public async Task<IReadOnlyList<SearchCollection>> SearchCollectionAsync(string name, string language, CancellationToken cancellationToken)
        {
            if (!IsEnable())
            {
                return new List<SearchCollection>();
            }

            var key = $"collectionsearch-{name}-{language}";
            if (this.memoryCache.TryGetValue(key, out SearchContainer<SearchCollection>? collections) && collections != null)
            {
                return collections.Results;
            }

            try
            {
                await this.EnsureClientConfigAsync().ConfigureAwait(false);

                var searchResults = await this.tmDbClient
                    .SearchCollectionAsync(name, NormalizeLanguage(language), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (searchResults.Results.Count > 0)
                {
                    this.memoryCache.Set(key, searchResults, TimeSpan.FromHours(CacheDurationInHours));
                }

                return searchResults.Results;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logTmdbError(this.logger, nameof(this.SearchCollectionAsync), ex);
                return new List<SearchCollection>();
            }
            catch (HttpRequestException ex)
            {
                this.logTmdbError(this.logger, nameof(this.SearchCollectionAsync), ex);
                return new List<SearchCollection>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        /// <summary>
        /// Gets the absolute URL of the poster.
        /// </summary>
        /// <param name="posterPath">The relative URL of the poster.</param>
        /// <returns>The absolute URL.</returns>
        public Uri? GetPosterUrl(string posterPath)
        {
            if (string.IsNullOrEmpty(posterPath))
            {
                return null;
            }

            return this.tmDbClient.GetImageUrl(this.tmDbClient.Config.Images.PosterSizes[^1], posterPath, true);
        }

        /// <summary>
        /// Gets the absolute URL of the backdrop image.
        /// </summary>
        /// <param name="posterPath">The relative URL of the backdrop image.</param>
        /// <returns>The absolute URL.</returns>
        public Uri? GetBackdropUrl(string posterPath)
        {
            if (string.IsNullOrEmpty(posterPath))
            {
                return null;
            }

            return this.tmDbClient.GetImageUrl(this.tmDbClient.Config.Images.BackdropSizes[^1], posterPath, true);
        }

        /// <summary>
        /// Gets the absolute URL of the profile image.
        /// </summary>
        /// <param name="actorProfilePath">The relative URL of the profile image.</param>
        /// <returns>The absolute URL.</returns>
        public Uri? GetProfileUrl(string actorProfilePath)
        {
            if (string.IsNullOrEmpty(actorProfilePath))
            {
                return null;
            }

            return this.tmDbClient.GetImageUrl(this.tmDbClient.Config.Images.ProfileSizes[^1], actorProfilePath, true);
        }

        /// <summary>
        /// Gets the absolute URL of the still image.
        /// </summary>
        /// <param name="filePath">The relative URL of the still image.</param>
        /// <returns>The absolute URL.</returns>
        public Uri? GetStillUrl(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            return this.tmDbClient.GetImageUrl(this.tmDbClient.Config.Images.StillSizes[^1], filePath, true);
        }

        public Uri? GetLogoUrl(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            return this.tmDbClient.GetImageUrl(this.tmDbClient.Config.Images.LogoSizes[^1], filePath, true);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose.
        /// </summary>
        /// <param name="disposing">Dispose all members.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.memoryCache.Dispose();
                this.tmDbClient.Dispose();
            }
        }

        /// <summary>
        /// Normalizes a language string for use with TMDb's language parameter.
        /// </summary>
        /// <param name="language">The language code.</param>
        /// <returns>The normalized language code.</returns>
        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                return language;
            }

            return ChineseLocalePolicy.CanonicalizeLanguage(language) ?? language;
        }

        private static string? GetImageLanguagesParam(string preferredLanguage)
        {
            if (string.IsNullOrEmpty(preferredLanguage))
            {
                return null;
            }

            var languages = new List<string>();

            if (!string.IsNullOrEmpty(preferredLanguage))
            {
                var parts = preferredLanguage.Split(',');
                foreach (var lang in parts)
                {
                    var l = NormalizeLanguage(lang);
                    if (string.IsNullOrWhiteSpace(l))
                    {
                        continue;
                    }

                    AddLanguageIfMissing(languages, l);

                    if (l.Length == 5)
                    {
                        AddLanguageIfMissing(languages, l.Substring(0, 2));
                    }
                }
            }

            AddLanguageIfMissing(languages, "null");

            if (!languages.Contains("en"))
            {
                AddLanguageIfMissing(languages, "en");
            }

            return string.Join(',', languages);
        }

        private static void AddLanguageIfMissing(List<string> languages, string language)
        {
            if (!languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            {
                languages.Add(language);
            }
        }

        private static bool IsEnable()
        {
            return MetaSharkPlugin.Instance?.Configuration.EnableTmdb ?? true;
        }

        private Task EnsureClientConfigAsync()
        {
            return !this.tmDbClient.HasConfig ? this.tmDbClient.GetConfigAsync() : Task.CompletedTask;
        }
    }
}
