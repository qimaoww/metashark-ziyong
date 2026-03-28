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
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Objects.TvShows;
    using MetadataProvider = MediaBrowser.Model.Entities.MetadataProvider;

    public class SeriesProvider : BaseProvider, IRemoteMetadataProvider<Series, SeriesInfo>
    {
        public SeriesProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeriesProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
        }

        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(searchInfo);
            this.Log($"GetSearchResults of [name]: {searchInfo.Name}");
            var result = new List<RemoteSearchResult>();
            if (string.IsNullOrEmpty(searchInfo.Name))
            {
                return result;
            }

            // 从douban搜索
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

            // 尝试从tmdb搜索
            if (Config.EnableTmdbSearch)
            {
                var tmdbList = await this.TmdbApi.SearchSeriesAsync(searchInfo.Name, searchInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                result.AddRange(tmdbList.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(x =>
                {
                    return new RemoteSearchResult
                    {
                        // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了和电影保持一致并唯一
                        ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), x.Id.ToString(CultureInfo.InvariantCulture) }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Tmdb}_{x.Id}" } },
                        Name = string.Format(CultureInfo.InvariantCulture, "[TMDB]{0}", x.Name ?? x.OriginalName),
                        ImageUrl = this.TmdbApi.GetPosterUrl(x.PosterPath)?.ToString(),
                        Overview = x.Overview,
                        ProductionYear = x.FirstAirDate?.Year,
                    };
                }));
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

            var sid = info.GetProviderId(DoubanProviderId);
            var tmdbId = info.GetProviderId(MetadataProvider.Tmdb);
            var metaSource = info.GetMetaSource(MetaSharkPlugin.ProviderId);

            // 注意：会存在元数据有tmdbId，但metaSource没值的情况（之前由TMDB插件刮削导致）
            var hasTmdbMeta = metaSource == MetaSource.Tmdb && !string.IsNullOrEmpty(tmdbId);
            var hasDoubanMeta = metaSource != MetaSource.Tmdb && !string.IsNullOrEmpty(sid);
            this.Log($"GetSeriesMetadata of [name]: {info.Name} [fileName]: {fileName} metaSource: {metaSource} EnableTmdb: {Config.EnableTmdb}");
            if (!hasDoubanMeta && !hasTmdbMeta)
            {
                // 自动扫描搜索匹配元数据
                sid = await this.GuessByDoubanAsync(info, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(sid) && Config.EnableTmdbMatch)
                {
                    tmdbId = await this.GuestByTmdbAsync(info, cancellationToken).ConfigureAwait(false);
                    metaSource = MetaSource.Tmdb;
                }
            }

            if (metaSource != MetaSource.Tmdb && !string.IsNullOrEmpty(sid))
            {
                this.Log($"GetSeriesMetadata of douban [sid]: {sid}");
                var subject = await this.DoubanApi.GetMovieAsync(sid, cancellationToken).ConfigureAwait(false);
                if (subject == null)
                {
                    if (!string.IsNullOrEmpty(tmdbId))
                    {
                        return await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
                    }

                    return result;
                }

                subject.Celebrities.Clear();
                foreach (var celebrity in await this.DoubanApi.GetCelebritiesBySidAsync(sid, cancellationToken).ConfigureAwait(false))
                {
                    subject.Celebrities.Add(celebrity);
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
                    item.SetProviderId(MetadataProvider.Tmdb, tmdbId);
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
                subject.LimitDirectorCelebrities.Take(Configuration.PluginConfiguration.MAXCASTMEMBERS).ToList().ForEach(c => result.AddPerson(new PersonInfo
                {
                    Name = c.Name,
                    Type = c.RoleType == PersonType.Director ? PersonKind.Director : PersonKind.Actor,
                    Role = c.Role,
                    ImageUrl = GetLocalProxyImageUrl(new Uri(c.Img, UriKind.Absolute)).ToString(),
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, c.Id } },
                }));

                return result;
            }

            if (metaSource == MetaSource.Tmdb && !string.IsNullOrEmpty(tmdbId))
            {
                return await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
            }

            this.Log($"匹配失败！可检查下年份是否与豆瓣一致，是否需要登录访问. [name]: {info.Name} [year]: {info.Year}");
            return result;
        }

        private async Task<MetadataResult<Series>> GetMetadataByTmdb(string? tmdbId, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>();
            if (string.IsNullOrEmpty(tmdbId))
            {
                return result;
            }

            this.Log($"GetSeriesMetadata of tmdb [id]: \"{tmdbId}\"");
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

            foreach (var person in this.GetPersons(tvShow))
            {
                result.AddPerson(person);
            }

            result.QueriedById = true;
            result.HasMetadata = true;
            return result;
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
                    this.Log($"Can not found tmdb [id] by imdb id: \"{imdb}\"");
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
                    this.Log($"Can not found tmdb [id] by name: \"{name}\" and year: \"{year}\"");
                }
            }

            return null;
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

            series.SetProviderId(MetadataProvider.Tmdb, seriesResult.Id.ToString(CultureInfo.InvariantCulture));

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
                    this.Log("TMDb tags added for series: id={0} name={1} count={2}", seriesResult.Id, seriesResult.Name, tagCount);
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
                this.Log("Set series tvdb id by tmdb external ids. tmdbId: {0} tvdbId: {1}", tmdbId, externalIds.TvdbId);
            }
        }

        private IEnumerable<PersonInfo> GetPersons(TvShow seriesResult)
        {
            // 演员
            if (seriesResult.Credits?.Cast != null)
            {
                foreach (var actor in seriesResult.Credits.Cast.OrderBy(a => a.Order).Take(Configuration.PluginConfiguration.MAXCASTMEMBERS))
                {
                    var personInfo = new PersonInfo
                    {
                        Name = actor.Name.Trim(),
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

                    yield return personInfo;
                }
            }

            // 导演
            if (seriesResult.Credits?.Crew != null)
            {
                var keepTypes = new[]
                {
                    PersonType.Director,
                    PersonType.Writer,
                    PersonType.Producer,
                };

                foreach (var person in seriesResult.Credits.Crew)
                {
                    // Normalize this
                    var type = MapCrewToPersonType(person);

                    if (!keepTypes.Contains(type, StringComparer.OrdinalIgnoreCase)
                        && !keepTypes.Contains(person.Job ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var personInfo = new PersonInfo
                    {
                        Name = person.Name.Trim(),
                        Role = person.Job,
                        Type = type == PersonType.Director ? PersonKind.Director : (type == PersonType.Producer ? PersonKind.Producer : PersonKind.Actor),
                    };

                    if (!string.IsNullOrWhiteSpace(person.ProfilePath))
                    {
                        personInfo.ImageUrl = this.TmdbApi.GetPosterUrl(person.ProfilePath)?.ToString();
                    }

                    if (person.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, person.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    yield return personInfo;
                }
            }
        }
    }
}
