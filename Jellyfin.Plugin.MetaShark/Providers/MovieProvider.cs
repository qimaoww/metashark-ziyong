// <copyright file="MovieProvider.cs" company="PlaceholderCompany">
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
    using AngleSharp.Text;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Workers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Objects.Search;

    public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>
    {
        private readonly IMovieSeriesPeopleOverwriteRefreshCandidateStore? movieSeriesPeopleOverwriteRefreshCandidateStore;

        public MovieProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi, IMovieSeriesPeopleOverwriteRefreshCandidateStore? movieSeriesPeopleOverwriteRefreshCandidateStore = null)
            : base(httpClientFactory, loggerFactory.CreateLogger<MovieProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
            this.movieSeriesPeopleOverwriteRefreshCandidateStore = movieSeriesPeopleOverwriteRefreshCandidateStore ?? InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore.Shared;
        }

        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(searchInfo);
            this.Log("开始搜索电影候选. name: {0}", searchInfo.Name);
            var result = new List<RemoteSearchResult>();
            if (string.IsNullOrEmpty(searchInfo.Name))
            {
                return result;
            }

            // 从douban搜索
            var res = await this.DoubanApi.SearchMovieAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
            result.AddRange(res.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(x =>
            {
                return new RemoteSearchResult
                {
                    // 注意：jellyfin 会判断电影所有 provider id 是否有相同的，有相同的值就会认为是同一影片，会被合并不返回，必须保持 provider id 的唯一性
                    // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了保持唯一
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, x.Sid }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_{x.Sid}" } },
                    ImageUrl = this.GetProxyImageUrl(new Uri(x.Img, UriKind.Absolute)).ToString(),
                    ProductionYear = x.Year,
                    Name = x.Name,
                };
            }));

            // 从tmdb搜索
            if (Config.EnableTmdbSearch)
            {
                var tmdbList = await this.TmdbApi.SearchMovieAsync(searchInfo.Name, searchInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                result.AddRange(tmdbList.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(x =>
                {
                    return new RemoteSearchResult
                    {
                        // 注意：jellyfin 会判断电影所有 provider id 是否有相同的，有相同的值就会认为是同一影片，会被合并不返回，必须保持 provider id 的唯一性
                        // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了保持唯一
                        ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), x.Id.ToString(CultureInfo.InvariantCulture) }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Tmdb}_{x.Id}" } },
                        Name = string.Format(CultureInfo.InvariantCulture, "[TMDB]{0}", x.Title ?? x.OriginalTitle),
                        ImageUrl = this.TmdbApi.GetPosterUrl(x.PosterPath)?.ToString(),
                        Overview = x.Overview,
                        ProductionYear = x.ReleaseDate?.Year,
                    };
                }));
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            var fileName = GetOriginalFileName(info);
            var result = new MetadataResult<Movie>();
            var semantic = this.ResolveMetadataSemantic(info);
            var doubanAllowed = IsDoubanAllowed(semantic);

            // 使用刷新元数据时，providerIds会保留旧有值，只有识别/新增才会没值
            var sid = info.GetProviderId(DoubanProviderId);
            var tmdbId = info.GetProviderId(MetadataProvider.Tmdb);
            var metaSource = info.GetMetaSource(MetaSharkPlugin.ProviderId);

            // 注意：会存在元数据有tmdbId，但metaSource没值的情况（之前由TMDB插件刮削导致）
            var hasTmdbMeta = metaSource == MetaSource.Tmdb && !string.IsNullOrEmpty(tmdbId);
            var hasDoubanMeta = doubanAllowed && metaSource != MetaSource.Tmdb && !string.IsNullOrEmpty(sid);
            this.Log("开始获取电影元数据. name: {0} fileName: {1} metaSource: {2} enableTmdb: {3}", info.Name, fileName, metaSource, Config.EnableTmdb);
            if (!hasDoubanMeta && !hasTmdbMeta)
            {
                // 处理extras影片
                var extraResult = this.HandleExtraType(info);
                if (extraResult != null)
                {
                    return extraResult;
                }

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
                this.Log("通过 Douban 获取电影元数据. sid: \"{0}\"", sid);
                var subject = await this.DoubanApi.GetMovieAsync(sid, cancellationToken).ConfigureAwait(false);
                if (subject == null)
                {
                    if (!string.IsNullOrEmpty(tmdbId))
                    {
                        return await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
                    }

                    return result;
                }

                var movie = new Movie
                {
                    // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了保持唯一
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, subject.Sid }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_{subject.Sid}" } },
                    Name = subject.Name,
                    OriginalTitle = subject.OriginalName,
                    CommunityRating = subject.Rating,
                    Overview = subject.Intro,
                    ProductionYear = subject.Year,
                    HomePageUrl = "https://www.douban.com",
                    Genres = subject.Genres.ToArray(),
                    PremiereDate = subject.ScreenTime,
                };
                if (!string.IsNullOrEmpty(subject.Imdb))
                {
                    var newImdbId = await this.CheckNewImdbID(subject.Imdb, cancellationToken).ConfigureAwait(false);
                    subject.Imdb = newImdbId;
                    movie.SetProviderId(MetadataProvider.Imdb, newImdbId);

                    // 通过imdb获取TMDB id
                    var newTmdbId = await this.GetTmdbIdByImdbAsync(subject.Imdb, info.MetadataLanguage, info, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(newTmdbId))
                    {
                        tmdbId = newTmdbId;
                        movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
                    }
                }

                // 尝试通过搜索匹配获取tmdbId
                if (string.IsNullOrEmpty(tmdbId) && subject.Year > 0)
                {
                    var newTmdbId = await this.GuestByTmdbAsync(subject.Name, subject.Year, info, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(newTmdbId))
                    {
                        tmdbId = newTmdbId;
                        movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
                    }
                }

                // 通过imdb获取电影系列信息
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    var belongCollection = await this.GetTmdbCollection(info, tmdbId, cancellationToken).ConfigureAwait(false);
                    if (belongCollection != null && !string.IsNullOrEmpty(belongCollection.Name))
                    {
                        movie.CollectionName = belongCollection.Name;
                    }
                }

                // 通过imdb获取电影分级信息
                if (Config.EnableTmdbOfficialRating && !string.IsNullOrEmpty(tmdbId))
                {
                    var officialRating = await this.GetTmdbOfficialRating(info, tmdbId, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(officialRating))
                    {
                        movie.OfficialRating = officialRating;
                    }
                }

                result.Item = movie;
                result.QueriedById = true;
                result.HasMetadata = true;

                if (!string.IsNullOrEmpty(tmdbId))
                {
                    var acceptedPeopleCount = await this.TryAddTmdbPeopleAsync(tmdbId, info, result, cancellationToken).ConfigureAwait(false);
                    if (acceptedPeopleCount > 0)
                    {
                        this.TryQueueSearchMissingMetadataOverwriteCandidate(info, acceptedPeopleCount);
                    }
                }

                return result;
            }

            if (!string.IsNullOrEmpty(tmdbId) && (metaSource == MetaSource.Tmdb || !doubanAllowed))
            {
                return await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
            }

            this.Log("电影匹配失败，可检查年份是否与豆瓣一致，或是否需要登录访问. name: {0} year: {1}", info.Name, info.Year);
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

        private async Task<MetadataResult<Movie>> GetMetadataByTmdb(string tmdbId, MovieInfo info, CancellationToken cancellationToken)
        {
            this.Log("通过 TMDb 获取电影元数据. tmdbId: \"{0}\"", tmdbId);
            var result = new MetadataResult<Movie>();
            var movieResult = await this.TmdbApi
                            .GetMovieAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                            .ConfigureAwait(false);

            if (movieResult == null)
            {
                return result;
            }

            var movie = new Movie
            {
                Name = movieResult.Title ?? movieResult.OriginalTitle,
                OriginalTitle = movieResult.OriginalTitle,
                Overview = movieResult.Overview?.Replace("\n\n", "\n", StringComparison.InvariantCulture),
                Tagline = movieResult.Tagline,
                ProductionLocations = movieResult.ProductionCountries.Select(pc => pc.Name).ToArray(),
            };

            if (Config.EnableTmdbTags && movieResult.Keywords?.Keywords != null)
            {
                var tagCount = movieResult.Keywords.Keywords.Count;
                for (var i = 0; i < movieResult.Keywords.Keywords.Count; i++)
                {
                    movie.AddTag(movieResult.Keywords.Keywords[i].Name);
                }

                if (tagCount > 0)
                {
                    this.Log("已写入电影 TMDb 标签. id={0} name={1} count={2}", movieResult.Id, movieResult.Title ?? movieResult.OriginalTitle, tagCount);
                }
            }

            result = new MetadataResult<Movie>
            {
                QueriedById = true,
                HasMetadata = true,
                ResultLanguage = info.MetadataLanguage,
                Item = movie,
            };

            movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
            movie.SetProviderId(MetadataProvider.Imdb, movieResult.ImdbId);

            // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了保持唯一
            movie.SetProviderId(MetaSharkPlugin.ProviderId, $"{MetaSource.Tmdb}_{tmdbId}");

            // 获取电影系列信息
            if (movieResult.BelongsToCollection != null)
            {
                movie.CollectionName = movieResult.BelongsToCollection.Name;
            }

            movie.CommunityRating = (float)System.Math.Round(movieResult.VoteAverage, 2);
            movie.OfficialRating = this.GetTmdbOfficialRatingByData(movieResult, info.MetadataCountryCode);
            movie.PremiereDate = movieResult.ReleaseDate;
            movie.ProductionYear = movieResult.ReleaseDate?.Year;

            if (movieResult.ProductionCompanies != null)
            {
                movie.SetStudios(movieResult.ProductionCompanies.Select(c => c.Name));
            }

            var genres = movieResult.Genres;

            foreach (var genre in genres.Select(g => g.Name))
            {
                movie.AddGenre(genre);
            }

            var acceptedPeopleCount = await this.AddTmdbPeopleAsync(movieResult, result, cancellationToken).ConfigureAwait(false);
            this.TryQueueSearchMissingMetadataOverwriteCandidate(info, acceptedPeopleCount);

            return result;
        }

        private async Task<int> TryAddTmdbPeopleAsync(string tmdbId, MovieInfo info, MetadataResult<Movie> result, CancellationToken cancellationToken)
        {
            var movieResult = await this.TmdbApi
                .GetMovieAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            if (movieResult == null)
            {
                return 0;
            }

            return await this.AddTmdbPeopleAsync(movieResult, result, cancellationToken).ConfigureAwait(false);
        }

        private MetadataResult<Movie>? HandleExtraType(MovieInfo info)
        {
            // 特典或extra视频可能和正片放在同一目录
            // TODO：插件暂时不支持设置影片为extra类型，只能直接忽略处理（最好放extras目录）
            var fileName = Path.GetFileNameWithoutExtension(info.Path) ?? info.Name;
            var parseResult = NameParser.Parse(fileName);
            if (parseResult.IsExtra)
            {
                this.Log("识别到影片特典，跳过处理. name: {0}", fileName);
                return new MetadataResult<Movie>();
            }

            // 动画常用特典文件夹
            if (NameParser.IsSpecialDirectory(info.Path) || NameParser.IsExtraDirectory(info.Path))
            {
                this.Log("识别到影片特典，跳过处理. name: {0}", fileName);
                return new MetadataResult<Movie>();
            }

            return null;
        }

        private void TryQueueSearchMissingMetadataOverwriteCandidate(MovieInfo info, int expectedPeopleCount)
        {
            if (this.movieSeriesPeopleOverwriteRefreshCandidateStore == null)
            {
                return;
            }

            if (this.ResolveMetadataSemantic(info) != DefaultScraperSemantic.UserRefresh)
            {
                return;
            }

            var movie = !string.IsNullOrWhiteSpace(info.Path)
                ? this.LibraryManager.FindByPath(info.Path, false) as Movie
                : null;
            var itemId = movie?.Id ?? Guid.Empty;
            var httpContext = this.HttpContextAccessor.HttpContext;
            if (itemId == Guid.Empty && !TryResolveItemIdFromRequestPath(httpContext, out itemId))
            {
                return;
            }

            var existingCandidate = this.movieSeriesPeopleOverwriteRefreshCandidateStore.Peek(itemId);
            if (existingCandidate?.OverwriteQueued == true)
            {
                return;
            }

            this.movieSeriesPeopleOverwriteRefreshCandidateStore.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = itemId,
                ItemPath = movie?.Path ?? info.Path ?? string.Empty,
                ExpectedPeopleCount = expectedPeopleCount,
            });
            this.Log("已记录单项影视人物 overwrite candidate. itemId: {0} expectedPeopleCount: {1}", itemId, expectedPeopleCount);
        }

        private async Task<int> AddTmdbPeopleAsync(TMDbLib.Objects.Movies.Movie movieResult, MetadataResult<Movie> result, CancellationToken cancellationToken)
        {
            var people = await this.GetPersonsAsync(movieResult, cancellationToken).ConfigureAwait(false);
            foreach (var person in people)
            {
                result.AddPerson(person);
            }

            return people.Count;
        }

        private async Task<IReadOnlyList<PersonInfo>> GetPersonsAsync(TMDbLib.Objects.Movies.Movie item, CancellationToken cancellationToken)
        {
            var persons = new List<PersonInfo>();

            // 演员
            if (item.Credits?.Cast != null)
            {
                var acceptedActorCount = 0;
                foreach (var actor in item.Credits.Cast.OrderBy(a => a.Order))
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

            // 导演
            if (item.Credits?.Crew != null)
            {
                var keepTypes = new[]
                {
                    PersonType.Director,
                    PersonType.Writer,
                    PersonType.Producer,
                };

                foreach (var person in item.Credits.Crew)
                {
                    // Normalize this
                    var type = MapCrewToPersonType(person);

                    if (!keepTypes.Contains(type, StringComparer.OrdinalIgnoreCase) &&
                            !keepTypes.Contains(person.Job ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var localizedName = await this.ResolveSimplifiedChineseOnlyItemPersonNameAsync(person.Name, person.Id, cancellationToken).ConfigureAwait(false);
                    if (localizedName == null)
                    {
                        continue;
                    }

                    var personInfo = new PersonInfo
                    {
                        Name = localizedName,
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

                    persons.Add(personInfo);
                }
            }

            return persons;
        }

        private string? GetTmdbOfficialRatingByData(TMDbLib.Objects.Movies.Movie? movieResult, string preferredCountryCode)
        {
            _ = this.Logger;
            if (movieResult == null || movieResult.Releases?.Countries == null)
            {
                return null;
            }

            var releases = movieResult.Releases.Countries.Where(i => !string.IsNullOrWhiteSpace(i.Certification)).ToList();

            var ourRelease = releases.FirstOrDefault(c => string.Equals(c.Iso_3166_1, preferredCountryCode, StringComparison.OrdinalIgnoreCase));
            var usRelease = releases.FirstOrDefault(c => string.Equals(c.Iso_3166_1, "US", StringComparison.OrdinalIgnoreCase));
            var minimumRelease = releases.FirstOrDefault();

            if (ourRelease != null)
            {
                var ratingPrefix = string.Equals(preferredCountryCode, "us", StringComparison.OrdinalIgnoreCase) ? string.Empty : preferredCountryCode + "-";
                var newRating = ratingPrefix + ourRelease.Certification;

                newRating = newRating.Replace("de-", "FSK-", StringComparison.OrdinalIgnoreCase);

                return newRating;
            }

            if (usRelease != null)
            {
                return usRelease.Certification;
            }

            return minimumRelease?.Certification;
        }

        private async Task<SearchCollection?> GetTmdbCollection(MovieInfo info, string tmdbId, CancellationToken cancellationToken)
        {
            var movieResult = await this.TmdbApi
                            .GetMovieAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                            .ConfigureAwait(false);
            if (movieResult != null)
            {
                return movieResult.BelongsToCollection;
            }

            return null;
        }

        private async Task<string?> GetTmdbOfficialRating(ItemLookupInfo info, string tmdbId, CancellationToken cancellationToken)
        {
            var movieResult = await this.TmdbApi
                            .GetMovieAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                            .ConfigureAwait(false);

            return this.GetTmdbOfficialRatingByData(movieResult, info.MetadataCountryCode);
        }
    }
}
