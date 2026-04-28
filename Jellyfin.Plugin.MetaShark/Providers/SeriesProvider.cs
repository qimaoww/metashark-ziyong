// <copyright file="SeriesProvider.cs" company="PlaceholderCompany">
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
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Workers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Objects.Search;
    using TMDbLib.Objects.TvShows;
    using MetadataProvider = MediaBrowser.Model.Entities.MetadataProvider;

    public class SeriesProvider : BaseProvider, IRemoteMetadataProvider<Series, SeriesInfo>
    {
        private readonly IMovieSeriesPeopleOverwriteRefreshCandidateStore? movieSeriesPeopleOverwriteRefreshCandidateStore;

        public SeriesProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi, IMovieSeriesPeopleOverwriteRefreshCandidateStore? movieSeriesPeopleOverwriteRefreshCandidateStore = null)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeriesProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
            this.movieSeriesPeopleOverwriteRefreshCandidateStore = movieSeriesPeopleOverwriteRefreshCandidateStore ?? InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore.Shared;
        }

        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(searchInfo);
            this.Log("开始搜索剧集候选. name: {0}", searchInfo.Name);
            var result = new List<RemoteSearchResult>();
            var hasExactTmdbHit = false;
            var hasUsableTitle = !string.IsNullOrWhiteSpace(searchInfo.Name);

            if (Config.EnableTmdb)
            {
                var tmdbIdStr = searchInfo.GetTmdbId();
                var formattedTmdbId = FormatProviderIdForLog(tmdbIdStr);
                if (string.IsNullOrWhiteSpace(tmdbIdStr))
                {
                    this.Log("跳过显式 TMDb ID 匹配，provider id 为空. rawProviderId: {0} fallbackToTitleSearch: {1}", formattedTmdbId, hasUsableTitle);
                }
                else if (!int.TryParse(tmdbIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
                {
                    this.Log("跳过显式 TMDb ID 匹配，provider id 无效. rawProviderId: {0} fallbackToTitleSearch: {1}", formattedTmdbId, hasUsableTitle);
                }
                else if (tmdbId <= 0)
                {
                    this.Log("跳过显式 TMDb ID 匹配，provider id 小于等于 0. rawProviderId: {0} fallbackToTitleSearch: {1}", formattedTmdbId, hasUsableTitle);
                }
                else
                {
                    this.Log("尝试显式 TMDb ID 精确匹配. tmdbId: {0}", tmdbId);
                    var tvShow = await this.TmdbApi.GetSeriesAsync(tmdbId, searchInfo.MetadataLanguage, searchInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                    if (tvShow != null)
                    {
                        result.Add(this.MapTmdbSeriesSearchResult(tvShow));
                        hasExactTmdbHit = true;
                    }
                    else
                    {
                        this.Log("显式 TMDb ID 未命中剧集，回退标题搜索. rawProviderId: {0} fallbackToTitleSearch: {1}", formattedTmdbId, hasUsableTitle);
                    }
                }
            }

            if (!hasUsableTitle)
            {
                return result;
            }

            // 从douban搜索
            if (IsDoubanAllowed(DefaultScraperSemantic.ManualSearch))
            {
                var res = await this.DoubanApi.SearchTVAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
                result.AddRange(res.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(x =>
                {
                    return new RemoteSearchResult
                    {
                        // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了和电影保持一致并唯一
                        ProviderIds = new Dictionary<string, string> { { DoubanProviderId, x.Sid }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_{x.Sid}" } },
                        ImageUrl = this.GetProxyImageUrl(new Uri(x.Img, UriKind.Absolute)).ToString(),
                        ProductionYear = x.Year,
                        Name = x.Name,
                    };
                }));
            }

            // 尝试从tmdb搜索
            if (Config.EnableTmdbSearch)
            {
                if (hasExactTmdbHit)
                {
                    this.Log("已命中显式 TMDb ID，跳过 TMDb 标题搜索");
                }
                else
                {
                    var tmdbList = await this.TmdbApi.SearchSeriesAsync(searchInfo.Name, searchInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                    result.AddRange(tmdbList.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(x => this.MapTmdbSeriesSearchResult(x)));
                }
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (string.IsNullOrEmpty(info.MetadataLanguage) && !string.IsNullOrEmpty(info.Name) && info.Name.HasChinese())
            {
                info.MetadataLanguage = "zh-CN";
            }

            var fileName = GetOriginalFileName(info);
            var result = new MetadataResult<Series>();
            var semantic = this.ResolveMetadataSemantic(info);
            var doubanAllowed = IsDoubanAllowed(semantic);

            var sid = info.GetProviderId(DoubanProviderId);
            var tmdbId = info.GetTmdbId();
            var metaSource = info.GetMetaSource(MetaSharkPlugin.ProviderId);

            // 注意：会存在元数据有tmdbId，但metaSource没值的情况（之前由TMDB插件刮削导致）
            var hasTmdbMeta = metaSource == MetaSource.Tmdb && !string.IsNullOrEmpty(tmdbId);
            var hasDoubanMeta = doubanAllowed && metaSource != MetaSource.Tmdb && !string.IsNullOrEmpty(sid);
            this.Log("开始获取剧集元数据. name: {0} fileName: {1} metaSource: {2} enableTmdb: {3}", info.Name, fileName, metaSource, Config.EnableTmdb);
            if (!hasDoubanMeta && !hasTmdbMeta)
            {
                // 自动扫描搜索匹配元数据
                if (doubanAllowed)
                {
                    sid = await this.GuessByDoubanAsync(info, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(sid) && Config.EnableTmdbMatch)
                {
                    tmdbId = await this.GuestByTmdbAsync(info, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(tmdbId))
                    {
                        metaSource = MetaSource.Tmdb;
                    }
                }
            }

            if (doubanAllowed && metaSource != MetaSource.Tmdb && !string.IsNullOrEmpty(sid))
            {
                this.Log("通过 Douban 获取剧集元数据. sid: {0}", sid);
                var subject = await this.DoubanApi.GetMovieAsync(sid, cancellationToken).ConfigureAwait(false);
                if (subject == null)
                {
                    if (!string.IsNullOrEmpty(tmdbId))
                    {
                        return await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
                    }

                    return result;
                }

                var seriesName = this.RemoveSeasonSuffix(subject.Name);
                var item = new Series
                {
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, subject.Sid }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_{subject.Sid}" } },
                    Name = seriesName,
                    OriginalTitle = this.RemoveSeasonSuffix(subject.OriginalName),
                    CommunityRating = subject.Rating,
                    Overview = subject.Intro,
                    ProductionYear = subject.Year,
                    HomePageUrl = "https://www.douban.com",
                    Genres = subject.Genres.ToArray(),
                    PremiereDate = subject.ScreenTime,
                    Tagline = string.Empty,
                };

                // 设置imdb元数据
                if (!string.IsNullOrEmpty(subject.Imdb))
                {
                    var newImdbId = await this.CheckNewImdbID(subject.Imdb, cancellationToken).ConfigureAwait(false);
                    subject.Imdb = newImdbId;
                    item.SetProviderId(MetadataProvider.Imdb, newImdbId);
                }

                // 搜索匹配tmdbId
                var newTmdbId = await this.FindTmdbId(seriesName, subject.Imdb, subject.Year, info, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(newTmdbId))
                {
                    tmdbId = newTmdbId;
                    item.SetProviderId(MetaSharkTmdbProviderId, tmdbId);
                }

                if (!string.IsNullOrEmpty(tmdbId))
                {
                    await this.TryPopulateTvExternalIdsFromTmdbAsync(item, tmdbId, info, cancellationToken).ConfigureAwait(false);
                }

                // 通过imdb获取电影分级信息
                if (Config.EnableTmdbOfficialRating && !string.IsNullOrEmpty(tmdbId))
                {
                    var officialRating = await this.GetTmdbOfficialRating(info, tmdbId, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(officialRating))
                    {
                        item.OfficialRating = officialRating;
                    }
                }

                result.Item = item;
                result.QueriedById = true;
                result.HasMetadata = true;

                if (!string.IsNullOrEmpty(tmdbId))
                {
                    var acceptedPeopleCount = await this.TryAddTmdbPeopleAsync(tmdbId, info, result, cancellationToken).ConfigureAwait(false);
                    this.TryQueueSearchMissingMetadataOverwriteCandidate(info, tmdbId, result.People, acceptedPeopleCount);
                }

                return result;
            }

            if (!string.IsNullOrEmpty(tmdbId) && (metaSource == MetaSource.Tmdb || !doubanAllowed))
            {
                return await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
            }

            this.Log("剧集匹配失败，可检查年份是否与豆瓣一致，或是否需要登录访问. name: {0} year: {1}", info.Name, info.Year);
            return result;
        }

        private static bool TryResolveItemIdFromRequestPath(HttpContext? httpContext, out Guid itemId)
        {
            itemId = Guid.Empty;

            var path = httpContext?.Request.Path.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2
                && string.Equals(segments[0], "Items", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[1], out itemId);
        }

        private static bool IsExplicitRefreshRequestWithoutReplaceAllMetadata(HttpContext? httpContext, Guid expectedItemId)
        {
            if (httpContext == null
                || expectedItemId == Guid.Empty
                || !HttpMethods.IsPost(httpContext.Request.Method))
            {
                return false;
            }

            var path = httpContext.Request.Path.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3
                || !string.Equals(segments[0], "Items", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(segments[1], out var requestItemId)
                || requestItemId != expectedItemId
                || !string.Equals(segments[2], "Refresh", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (httpContext.Request.Query.TryGetValue("ReplaceAllMetadata", out var replaceAllMetadataValues)
                && bool.TryParse(replaceAllMetadataValues.ToString(), out var replaceAllMetadata)
                && replaceAllMetadata)
            {
                return false;
            }

            return true;
        }

        private static bool ShouldRearmQueuedOverwriteCandidate(DefaultScraperSemantic semantic, HttpContext? httpContext, Guid expectedItemId)
        {
            if (semantic == DefaultScraperSemantic.ManualMatch)
            {
                return true;
            }

            if (semantic != DefaultScraperSemantic.UserRefresh)
            {
                return false;
            }

            return httpContext == null || IsExplicitRefreshRequestWithoutReplaceAllMetadata(httpContext, expectedItemId);
        }

        private static string FormatProviderIdForLog(string? providerId)
        {
            if (providerId == null)
            {
                return "'<null>'";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "'{0}'",
                providerId
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal)
                    .Replace("\t", "\\t", StringComparison.Ordinal)
                    .Replace(" ", "\\u0020", StringComparison.Ordinal));
        }

        private RemoteSearchResult MapTmdbSeriesSearchResult(SearchTv searchResult)
        {
            return this.MapTmdbSeriesSearchResult(
                searchResult.Id,
                searchResult.Name,
                searchResult.OriginalName,
                searchResult.PosterPath,
                searchResult.Overview,
                searchResult.FirstAirDate);
        }

        private RemoteSearchResult MapTmdbSeriesSearchResult(TvShow seriesResult)
        {
            return this.MapTmdbSeriesSearchResult(
                seriesResult.Id,
                seriesResult.Name,
                seriesResult.OriginalName,
                seriesResult.PosterPath,
                seriesResult.Overview,
                seriesResult.FirstAirDate);
        }

        private RemoteSearchResult MapTmdbSeriesSearchResult(int tmdbId, string? name, string? originalName, string? posterPath, string? overview, DateTime? firstAirDate)
        {
            return new RemoteSearchResult
            {
                // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了和电影保持一致并唯一
                ProviderIds = new Dictionary<string, string>
                {
                    { MetaSharkPlugin.ProviderId, $"Tmdb_{tmdbId}" },
                },
                Name = string.Format(CultureInfo.InvariantCulture, "[TMDB]{0}", name ?? originalName),
                ImageUrl = string.IsNullOrEmpty(posterPath) ? null : this.TmdbApi.GetPosterUrl(posterPath)?.ToString(),
                Overview = overview,
                ProductionYear = firstAirDate?.Year,
            };
        }

        private async Task<MetadataResult<Series>> GetMetadataByTmdb(string? tmdbId, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>();
            if (string.IsNullOrEmpty(tmdbId))
            {
                return result;
            }

            this.Log("通过 TMDb 获取剧集元数据. tmdbId: \"{0}\"", tmdbId);
            var tvShow = await this.TmdbApi
                .GetSeriesAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            if (tvShow == null)
            {
                return result;
            }

            result = new MetadataResult<Series>
            {
                Item = this.MapTvShowToSeries(tvShow, info.MetadataCountryCode),
                ResultLanguage = info.MetadataLanguage ?? tvShow.OriginalLanguage,
            };

            var acceptedPeopleCount = await this.AddTmdbPeopleAsync(tvShow, result, cancellationToken).ConfigureAwait(false);
            this.TryQueueSearchMissingMetadataOverwriteCandidate(info, tmdbId, result.People, acceptedPeopleCount);

            result.QueriedById = true;
            result.HasMetadata = true;
            return result;
        }

        private async Task<int> TryAddTmdbPeopleAsync(string tmdbId, ItemLookupInfo info, MetadataResult<Series> result, CancellationToken cancellationToken)
        {
            if (!int.TryParse(tmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbNumericId))
            {
                return 0;
            }

            var tvShow = await this.TmdbApi
                .GetSeriesAsync(tmdbNumericId, info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            if (tvShow == null)
            {
                return 0;
            }

            return await this.AddTmdbPeopleAsync(tvShow, result, cancellationToken).ConfigureAwait(false);
        }

        private async Task<int> AddTmdbPeopleAsync(TvShow tvShow, MetadataResult<Series> result, CancellationToken cancellationToken)
        {
            var people = await this.GetPersonsAsync(tvShow, cancellationToken).ConfigureAwait(false);
            foreach (var person in people)
            {
                result.AddPerson(person);
            }

            return people.Count;
        }

        private async Task<string?> FindTmdbId(string name, string imdb, int? year, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            // 通过imdb获取TMDB id
            if (!string.IsNullOrEmpty(imdb))
            {
                var tmdbId = await this.GetTmdbIdByImdbAsync(imdb, info.MetadataLanguage, info, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    return tmdbId;
                }
                else
                {
                    this.Log("未找到 TMDb id. imdbId: \"{0}\"", imdb);
                }
            }

            // 尝试通过搜索匹配获取tmdbId
            if (!string.IsNullOrEmpty(name) && year != null && year > 0)
            {
                var tmdbId = await this.GuestByTmdbAsync(name, year, info, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    return tmdbId;
                }
                else
                {
                    this.Log("未找到 TMDb id. name: \"{0}\" year: \"{1}\"", name, year);
                }
            }

            return null;
        }

        private void TryQueueSearchMissingMetadataOverwriteCandidate(ItemLookupInfo info, string tmdbId, IEnumerable<PersonInfo>? authoritativePeople, int expectedPeopleCount)
        {
            if (this.movieSeriesPeopleOverwriteRefreshCandidateStore == null)
            {
                return;
            }

            var httpContext = this.HttpContextAccessor.HttpContext;
            var semantic = this.ResolveMetadataSemantic(info);
            if (!SupportsSearchMissingMetadataOverwriteCandidate(semantic))
            {
                return;
            }

            var series = !string.IsNullOrWhiteSpace(info.Path)
                ? this.LibraryManager.FindByPath(info.Path, true) as Series
                : null;
            var authoritativePeopleSnapshot = CreateTmdbAuthoritativePeopleSnapshot(nameof(Series), tmdbId, authoritativePeople);
            if (authoritativePeopleSnapshot == null
                || !RequiresSearchMissingMetadataOverwriteCandidate(series, authoritativePeopleSnapshot))
            {
                return;
            }

            var itemId = series?.Id ?? Guid.Empty;
            if (itemId == Guid.Empty && !TryResolveItemIdFromRequestPath(httpContext, out itemId))
            {
                return;
            }

            var existingCandidate = this.movieSeriesPeopleOverwriteRefreshCandidateStore.Peek(itemId);
            if (existingCandidate?.OverwriteQueued == true
                && !ShouldRearmQueuedOverwriteCandidate(semantic, httpContext, itemId))
            {
                return;
            }

            this.movieSeriesPeopleOverwriteRefreshCandidateStore.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = itemId,
                ItemPath = series?.Path ?? info.Path ?? string.Empty,
                ExpectedPeopleCount = expectedPeopleCount,
                AuthoritativePeopleSnapshot = authoritativePeopleSnapshot,
            });
            this.Log(
                "已记录单项影视人物 overwrite candidate. itemId: {0} semantic: {1} expectedPeopleCount: {2} authoritativePeopleCount: {3}",
                itemId,
                semantic,
                expectedPeopleCount,
                authoritativePeopleSnapshot.People.Count);
        }

        private string? GetTmdbOfficialRatingByData(TvShow? tvShow, string preferredCountryCode)
        {
            _ = this.Logger;
            if (tvShow != null)
            {
                var contentRatings = tvShow.ContentRatings.Results ?? new List<ContentRating>();

                var ourRelease = contentRatings.FirstOrDefault(c => string.Equals(c.Iso_3166_1, preferredCountryCode, StringComparison.OrdinalIgnoreCase));
                var usRelease = contentRatings.FirstOrDefault(c => string.Equals(c.Iso_3166_1, "US", StringComparison.OrdinalIgnoreCase));
                var minimumRelease = contentRatings.FirstOrDefault();

                if (ourRelease != null)
                {
                    return ourRelease.Rating;
                }
                else if (usRelease != null)
                {
                    return usRelease.Rating;
                }
                else if (minimumRelease != null)
                {
                    return minimumRelease.Rating;
                }
            }

            return null;
        }

        private Series MapTvShowToSeries(TvShow seriesResult, string preferredCountryCode)
        {
            var series = new Series
            {
                Name = seriesResult.Name,
                OriginalTitle = seriesResult.OriginalName,
            };

            series.SetProviderId(MetaSharkTmdbProviderId, seriesResult.Id.ToString(CultureInfo.InvariantCulture));

            series.CommunityRating = (float)System.Math.Round(seriesResult.VoteAverage, 2);

            series.Overview = seriesResult.Overview;

            if (seriesResult.Networks != null)
            {
                series.Studios = seriesResult.Networks.Select(i => i.Name).ToArray();
            }

            if (seriesResult.Genres != null)
            {
                series.Genres = seriesResult.Genres.Select(i => i.Name).ToArray();
            }

            if (Config.EnableTmdbTags && seriesResult.Keywords?.Results != null)
            {
                var tagCount = seriesResult.Keywords.Results.Count;
                for (var i = 0; i < seriesResult.Keywords.Results.Count; i++)
                {
                    series.AddTag(seriesResult.Keywords.Results[i].Name);
                }

                if (tagCount > 0)
                {
                    this.Log("已写入剧集 TMDb 标签. id={0} name={1} count={2}", seriesResult.Id, seriesResult.Name, tagCount);
                }
            }

            series.HomePageUrl = seriesResult.Homepage;

            series.RunTimeTicks = seriesResult.EpisodeRunTime.Select(i => TimeSpan.FromMinutes(i).Ticks).FirstOrDefault();

            if (string.Equals(seriesResult.Status, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                series.Status = SeriesStatus.Ended;
                series.EndDate = seriesResult.LastAirDate;
            }
            else
            {
                series.Status = SeriesStatus.Continuing;
            }

            series.PremiereDate = seriesResult.FirstAirDate;
            series.ProductionYear = seriesResult.FirstAirDate?.Year;

            var ids = seriesResult.ExternalIds;
            if (ids != null)
            {
                if (!string.IsNullOrWhiteSpace(ids.ImdbId))
                {
                    series.SetProviderId(MetadataProvider.Imdb, ids.ImdbId);
                }

                if (!string.IsNullOrEmpty(ids.TvrageId))
                {
                    series.SetProviderId(MetadataProvider.TvRage, ids.TvrageId);
                }

                if (!string.IsNullOrEmpty(ids.TvdbId))
                {
                    series.SetProviderId(MetadataProvider.Tvdb, ids.TvdbId);
                }
            }

            series.SetProviderId(MetaSharkPlugin.ProviderId, $"{MetaSource.Tmdb}_{seriesResult.Id}");
            series.OfficialRating = this.GetTmdbOfficialRatingByData(seriesResult, preferredCountryCode);

            return series;
        }

        private async Task<string?> GetTmdbOfficialRating(ItemLookupInfo info, string tmdbId, CancellationToken cancellationToken)
        {
            var tvShow = await this.TmdbApi
                            .GetSeriesAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                            .ConfigureAwait(false);
            return this.GetTmdbOfficialRatingByData(tvShow, info.MetadataCountryCode);
        }

        private async Task TryPopulateTvExternalIdsFromTmdbAsync(Series series, string tmdbId, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            if (!int.TryParse(tmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbNumericId))
            {
                return;
            }

            var tvShow = await this.TmdbApi
                .GetSeriesAsync(tmdbNumericId, info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            var externalIds = tvShow?.ExternalIds;
            if (externalIds == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(series.GetProviderId(MetadataProvider.Imdb)) && !string.IsNullOrWhiteSpace(externalIds.ImdbId))
            {
                series.SetProviderId(MetadataProvider.Imdb, externalIds.ImdbId);
            }

            if (!string.IsNullOrWhiteSpace(externalIds.TvdbId))
            {
                series.SetProviderId(MetadataProvider.Tvdb, externalIds.TvdbId);
                this.Log("已通过 TMDb 外部 ID 写入剧集 TVDB id. tmdbId: {0} tvdbId: {1}", tmdbId, externalIds.TvdbId);
            }
        }

        private async Task<IReadOnlyList<PersonInfo>> GetPersonsAsync(TvShow seriesResult, CancellationToken cancellationToken)
        {
            var persons = new List<PersonInfo>();

            if (seriesResult.AggregateCredits?.Cast != null && seriesResult.AggregateCredits.Cast.Count > 0)
            {
                var acceptedActorCount = 0;

                foreach (var actor in seriesResult.AggregateCredits.Cast)
                {
                    if (acceptedActorCount >= Configuration.PluginConfiguration.MAXCASTMEMBERS)
                    {
                        break;
                    }

                    var localizedName = await this.ResolveSimplifiedChineseOnlyItemPersonNameAsync(actor.Name, actor.Id, cancellationToken).ConfigureAwait(false);
                    if (localizedName == null)
                    {
                        continue;
                    }

                    var personInfo = new PersonInfo
                    {
                        Name = localizedName,
                        Role = actor.Roles?.FirstOrDefault(role => !string.IsNullOrWhiteSpace(role.Character))?.Character,
                        Type = PersonKind.Actor,
                        SortOrder = acceptedActorCount,
                    };

                    if (!string.IsNullOrWhiteSpace(actor.ProfilePath))
                    {
                        personInfo.ImageUrl = this.TmdbApi.GetProfileUrl(actor.ProfilePath)?.ToString();
                    }

                    if (actor.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, actor.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    persons.Add(personInfo);
                    acceptedActorCount++;
                }

                return persons;
            }

            // 演员
            if (seriesResult.Credits?.Cast != null)
            {
                var acceptedActorCount = 0;

                foreach (var actor in seriesResult.Credits.Cast.OrderBy(a => a.Order))
                {
                    if (acceptedActorCount >= Configuration.PluginConfiguration.MAXCASTMEMBERS)
                    {
                        break;
                    }

                    var localizedName = await this.ResolveSimplifiedChineseOnlyItemPersonNameAsync(actor.Name, actor.Id, cancellationToken).ConfigureAwait(false);
                    if (localizedName == null)
                    {
                        continue;
                    }

                    var personInfo = new PersonInfo
                    {
                        Name = localizedName,
                        Role = actor.Character,
                        Type = PersonKind.Actor,
                        SortOrder = actor.Order,
                    };

                    if (!string.IsNullOrWhiteSpace(actor.ProfilePath))
                    {
                        personInfo.ImageUrl = this.TmdbApi.GetProfileUrl(actor.ProfilePath)?.ToString();
                    }

                    if (actor.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, actor.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    persons.Add(personInfo);
                    acceptedActorCount++;
                }
            }

            return persons;
        }
    }
}
