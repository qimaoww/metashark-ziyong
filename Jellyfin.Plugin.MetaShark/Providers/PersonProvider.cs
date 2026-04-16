// <copyright file="PersonProvider.cs" company="PlaceholderCompany">
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
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Objects.Find;

    /// <summary>
    /// OddbPersonProvider.
    /// </summary>
    public class PersonProvider : BaseProvider, IRemoteMetadataProvider<Person, PersonLookupInfo>
    {
        public PersonProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<PersonProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
        }

        /// <inheritdoc />
        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(PersonLookupInfo searchInfo, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(searchInfo);
            this.Log($"GetPersonSearchResults of [name]: {searchInfo.Name}");

            var result = new List<RemoteSearchResult>();
            if (!IsDoubanAllowed(DefaultScraperSemantic.ManualSearch))
            {
                return result;
            }

            var cid = searchInfo.GetProviderId(DoubanProviderId);
            if (!string.IsNullOrEmpty(cid))
            {
                var celebrity = await this.DoubanApi.GetCelebrityAsync(cid, cancellationToken).ConfigureAwait(false);
                if (celebrity != null)
                {
                    result.Add(new RemoteSearchResult
                    {
                        SearchProviderName = DoubanProviderName,
                        ProviderIds = new Dictionary<string, string> { { DoubanProviderId, celebrity.Id } },
                        ImageUrl = this.GetProxyImageUrl(new Uri(celebrity.Img, UriKind.Absolute)).ToString(),
                        Name = celebrity.Name,
                    });

                    return result;
                }
            }

            var res = await this.DoubanApi.SearchCelebrityAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
            result.AddRange(res.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(x =>
            {
                return new RemoteSearchResult
                {
                    SearchProviderName = DoubanProviderName,
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, x.Id } },
                    ImageUrl = this.GetProxyImageUrl(new Uri(x.Img, UriKind.Absolute)).ToString(),
                    Name = x.Name,
                };
            }));

            return result;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Person>> GetMetadata(PersonLookupInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            var result = new MetadataResult<Person>();
            var semantic = this.ResolveMetadataSemantic(info);
            var doubanAllowed = IsDoubanAllowed(semantic);

            var cid = info.GetProviderId(DoubanProviderId);
            this.Log($"GetPersonMetadata of [name]: {info.Name} [cid]: {cid}");
            if (doubanAllowed && !string.IsNullOrEmpty(cid))
            {
                var c = await this.DoubanApi.GetCelebrityAsync(cid, cancellationToken).ConfigureAwait(false);
                if (c != null)
                {
                    var item = new Person
                    {
                        // Name = c.Name.Trim(),  // 名称需保持和info.Name一致，不然会导致关联不到影片，自动被删除
                        OriginalTitle = c.DisplayOriginalName,  // 外国人显示英文名
                        HomePageUrl = c.Site,
                        Overview = c.Intro,
                    };
                    if (DateTime.TryParseExact(c.Birthdate, "yyyy年MM月dd日", null, DateTimeStyles.None, out var premiereDate))
                    {
                        item.PremiereDate = premiereDate;
                        item.ProductionYear = premiereDate.Year;
                    }

                    if (DateTime.TryParseExact(c.Enddate, "yyyy年MM月dd日", null, DateTimeStyles.None, out var endDate))
                    {
                        item.EndDate = endDate;
                    }

                    if (!string.IsNullOrWhiteSpace(c.Birthplace))
                    {
                        item.ProductionLocations = new[] { c.Birthplace };
                    }

                    item.SetProviderId(DoubanProviderId, c.Id);
                    if (!string.IsNullOrEmpty(c.Imdb))
                    {
                        var newImdbId = await this.ImdbApi.CheckPersonNewIDAsync(c.Imdb, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(newImdbId))
                        {
                            c.Imdb = newImdbId;
                        }

                        item.SetProviderId(MetadataProvider.Imdb, c.Imdb);

                        // 通过imdb获取TMDB id
                        var findResult = await this.TmdbApi.FindByExternalIdAsync(c.Imdb, FindExternalSource.Imdb, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                        if (findResult?.PersonResults != null && findResult.PersonResults.Count > 0)
                        {
                            var foundTmdbId = findResult.PersonResults.First().Id.ToString(CultureInfo.InvariantCulture);
                            this.Log($"GetPersonMetadata of found tmdb [id]: {foundTmdbId}");
                            item.SetProviderId(MetadataProvider.Tmdb, $"{foundTmdbId}");
                        }
                    }

                    result.QueriedById = true;
                    result.HasMetadata = true;
                    result.Item = item;

                    return result;
                }
            }

            // jellyfin强制最后一定使用默认的TheMovieDb插件获取一次，这里不太必要（除了使用自己的域名）
            var personTmdbId = info.GetProviderId(MetadataProvider.Tmdb);
            this.Log($"GetPersonMetadata of [personTmdbId]: {personTmdbId}");
            if (!string.IsNullOrEmpty(personTmdbId))
            {
                return await this.GetMetadataByTmdb(personTmdbId.ToInt(), info, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        public async Task<MetadataResult<Person>> GetMetadataByTmdb(int personTmdbId, PersonLookupInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Person>();
            var person = await this.TmdbApi.GetPersonAsync(personTmdbId, cancellationToken).ConfigureAwait(false);
            if (person != null)
            {
                var item = new Person
                {
                    // Name = info.Name.Trim(),   // 名称需保持和info.Name一致，不然会导致关联不到影片，自动被删除
                    HomePageUrl = person.Homepage,
                    Overview = person.Biography,
                    PremiereDate = person.Birthday?.ToUniversalTime(),
                    EndDate = person.Deathday?.ToUniversalTime(),
                };

                if (!string.IsNullOrWhiteSpace(person.PlaceOfBirth))
                {
                    item.ProductionLocations = new[] { person.PlaceOfBirth };
                }

                item.SetProviderId(MetadataProvider.Tmdb, person.Id.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(person.ImdbId))
                {
                    item.SetProviderId(MetadataProvider.Imdb, person.ImdbId);
                }

                result.HasMetadata = true;
                result.Item = item;

                return result;
            }

            return result;
        }
    }
}
