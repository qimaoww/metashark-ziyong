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

    /// <summary>
    /// OddbPersonProvider.
    /// </summary>
    public class PersonProvider : BaseProvider, IRemoteMetadataProvider<Person, PersonLookupInfo>
    {
        private static readonly (string Language, string? CountryCode)[] SimplifiedChineseBiographyPriority = new[]
        {
            ("zh", "CN"),
            ("zh-Hans", null),
            ("zh", null),
        };

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
            this.Log("开始搜索人物候选. name: {0}", searchInfo.Name);

            var result = new List<RemoteSearchResult>();
            var personTmdbId = searchInfo.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrEmpty(personTmdbId))
            {
                var person = await this.TmdbApi.GetPersonAsync(personTmdbId.ToInt(), cancellationToken).ConfigureAwait(false);
                if (person != null)
                {
                    result.Add(new RemoteSearchResult
                    {
                        SearchProviderName = TmdbProviderName,
                        ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), person.Id.ToString(CultureInfo.InvariantCulture) } },
                        ImageUrl = this.TmdbApi.GetProfileUrl(person.ProfilePath)?.ToString(),
                        Name = person.Name,
                    });
                }

                return result;
            }

            if (string.IsNullOrWhiteSpace(searchInfo.Name))
            {
                return result;
            }

            var searchResults = await this.TmdbApi.SearchPersonAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
            result.AddRange(searchResults.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(person =>
            {
                return new RemoteSearchResult
                {
                    SearchProviderName = TmdbProviderName,
                    ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), person.Id.ToString(CultureInfo.InvariantCulture) } },
                    ImageUrl = this.TmdbApi.GetProfileUrl(person.ProfilePath)?.ToString(),
                    Name = person.Name,
                };
            }));

            return result;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Person>> GetMetadata(PersonLookupInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            var result = new MetadataResult<Person>();
            var personTmdbId = info.GetProviderId(MetadataProvider.Tmdb);
            this.Log("通过 TMDb 获取人物元数据. personTmdbId: {0}", personTmdbId);
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
                    PremiereDate = person.Birthday?.ToUniversalTime(),
                    EndDate = person.Deathday?.ToUniversalTime(),
                };

                var overview = await this.GetPreferredSimplifiedChineseBiographyAsync(personTmdbId, cancellationToken).ConfigureAwait(false);
                if (overview != null)
                {
                    item.Overview = overview;
                }

                if (!string.IsNullOrWhiteSpace(person.PlaceOfBirth))
                {
                    item.ProductionLocations = new[] { person.PlaceOfBirth };
                }

                var profileUrl = this.TmdbApi.GetProfileUrl(person.ProfilePath)?.ToString();
                if (!string.IsNullOrWhiteSpace(profileUrl))
                {
                    item.SetImagePath(ImageType.Primary, profileUrl);
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

        private static string? TryAcceptSimplifiedChineseBiography(string? biography)
        {
            if (string.IsNullOrWhiteSpace(biography))
            {
                return null;
            }

            var trimmedBiography = biography.Trim();
            return ChineseLocalePolicy.IsTextAllowedForStrictZhCn(trimmedBiography)
                ? trimmedBiography
                : null;
        }

        private async Task<string?> GetPreferredSimplifiedChineseBiographyAsync(int personTmdbId, CancellationToken cancellationToken)
        {
            foreach (var localization in SimplifiedChineseBiographyPriority)
            {
                var person = await this.TmdbApi.GetPersonAsync(personTmdbId, localization.Language, localization.CountryCode, cancellationToken).ConfigureAwait(false);
                var biography = TryAcceptSimplifiedChineseBiography(person?.Biography);
                if (biography != null)
                {
                    return biography;
                }
            }

            return null;
        }
    }
}
