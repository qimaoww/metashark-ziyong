// <copyright file="PersonNameResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Globalization;
    using TMDbLib.Objects.General;

    internal sealed class PersonNameResolver
    {
        private readonly TmdbApi tmdbApi;
        private readonly ILibraryManager libraryManager;

        public PersonNameResolver(TmdbApi tmdbApi, ILibraryManager libraryManager)
        {
            ArgumentNullException.ThrowIfNull(tmdbApi);
            ArgumentNullException.ThrowIfNull(libraryManager);

            this.tmdbApi = tmdbApi;
            this.libraryManager = libraryManager;
        }

        public Scope CreateScope()
        {
            return new Scope(this.tmdbApi, this.libraryManager);
        }

        internal sealed class Scope
        {
            private readonly TmdbApi tmdbApi;
            private readonly ILibraryManager libraryManager;
            private readonly Dictionary<int, string?> localizedNamesByTmdbId = new Dictionary<int, string?>();
            private Dictionary<string, Person>? peopleByTmdbId;

            public Scope(TmdbApi tmdbApi, ILibraryManager libraryManager)
            {
                this.tmdbApi = tmdbApi;
                this.libraryManager = libraryManager;
            }

            public async Task<string> ResolveItemPersonNameAsync(string? currentCreditsName, int personTmdbId, CancellationToken cancellationToken)
            {
                return (await this.ResolveItemPersonNameCoreAsync(currentCreditsName, personTmdbId, allowRawNameFallback: true, cancellationToken).ConfigureAwait(false)) ?? string.Empty;
            }

            public Task<string?> ResolveSimplifiedChineseOnlyItemPersonNameAsync(string? currentCreditsName, int personTmdbId, CancellationToken cancellationToken)
            {
                return this.ResolveItemPersonNameCoreAsync(currentCreditsName, personTmdbId, allowRawNameFallback: false, cancellationToken);
            }

            private static bool IsMatchingPeopleTranslationLanguage(Translation translation, string requestedLanguage)
            {
                var translationLanguage = BuildTranslationLanguageTag(translation);
                return !string.IsNullOrWhiteSpace(translationLanguage)
                    && string.Equals(translationLanguage, requestedLanguage, StringComparison.OrdinalIgnoreCase);
            }

            private static string? BuildTranslationLanguageTag(Translation translation)
            {
                var language = GetTrimmedNonEmptyText(translation.Iso_639_1);
                var locale = GetTrimmedNonEmptyText(translation.Iso_3166_1);
                if (language == null)
                {
                    return null;
                }

                return locale == null
                    ? ChineseLocalePolicy.CanonicalizeLanguage(language)
                    : ChineseLocalePolicy.CanonicalizeLanguage($"{language}-{locale}");
            }

            private static string? GetTrimmedNonEmptyText(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                return value.Trim();
            }

            private async Task<string?> ResolveItemPersonNameCoreAsync(string? currentCreditsName, int personTmdbId, bool allowRawNameFallback, CancellationToken cancellationToken)
            {
                if (personTmdbId > 0)
                {
                    var localizedName = await this.GetPreferredZhCnPersonNameAsync(personTmdbId, cancellationToken).ConfigureAwait(false);
                    if (localizedName != null)
                    {
                        await this.TryAlignExistingLibraryPersonNameAsync(personTmdbId, localizedName, cancellationToken).ConfigureAwait(false);
                        return localizedName;
                    }
                }

                if (allowRawNameFallback)
                {
                    var acceptedRawName = GetTrimmedNonEmptyText(currentCreditsName);
                    if (acceptedRawName != null)
                    {
                        return acceptedRawName;
                    }

                    return string.Empty;
                }

                return null;
            }

            private async Task<string?> GetPreferredZhCnPersonNameAsync(int personTmdbId, CancellationToken cancellationToken)
            {
                if (this.localizedNamesByTmdbId.TryGetValue(personTmdbId, out var cachedName))
                {
                    return cachedName;
                }

                var localizedName = await this.LoadPreferredZhCnPersonNameAsync(personTmdbId, cancellationToken).ConfigureAwait(false);
                this.localizedNamesByTmdbId[personTmdbId] = localizedName;
                return localizedName;
            }

            private async Task<string?> LoadPreferredZhCnPersonNameAsync(int personTmdbId, CancellationToken cancellationToken)
            {
                var person = await this.tmdbApi.GetPersonAsync(personTmdbId, "zh-CN", null, cancellationToken).ConfigureAwait(false);
                var localizedName = GetTrimmedNonEmptyText(person?.Name);
                if (localizedName != null)
                {
                    return localizedName;
                }

                var translations = await this.tmdbApi.GetPersonTranslationsAsync(personTmdbId, cancellationToken).ConfigureAwait(false);
                if (translations?.Translations == null)
                {
                    return null;
                }

                foreach (var translation in translations.Translations)
                {
                    if (!IsMatchingPeopleTranslationLanguage(translation, "zh-CN"))
                    {
                        continue;
                    }

                    var translatedName = GetTrimmedNonEmptyText(translation.Data?.Name);
                    if (translatedName != null)
                    {
                        return translatedName;
                    }
                }

                return null;
            }

            private async Task TryAlignExistingLibraryPersonNameAsync(int personTmdbId, string resolvedName, CancellationToken cancellationToken)
            {
                var normalizedResolvedName = GetTrimmedNonEmptyText(resolvedName);
                if (personTmdbId <= 0 || normalizedResolvedName == null)
                {
                    return;
                }

                var existingPerson = this.FindExistingLibraryPersonByTmdbId(personTmdbId);
                if (existingPerson == null)
                {
                    return;
                }

                if (!MetadataLockGuard.CanWriteField(existingPerson, MetadataField.Name))
                {
                    return;
                }

                var currentExistingName = GetTrimmedNonEmptyText(existingPerson.Name);
                if (string.Equals(currentExistingName, normalizedResolvedName, StringComparison.Ordinal))
                {
                    return;
                }

                existingPerson.Name = normalizedResolvedName;
                var updateReason = existingPerson.OnMetadataChanged();
                await existingPerson.UpdateToRepositoryAsync(updateReason, cancellationToken).ConfigureAwait(false);
            }

            private Person? FindExistingLibraryPersonByTmdbId(int personTmdbId)
            {
                var tmdbProviderId = personTmdbId.ToString(CultureInfo.InvariantCulture);
                return this.EnsurePeopleByTmdbId().TryGetValue(tmdbProviderId, out var person) ? person : null;
            }

            /// <summary>
            /// 全库 Person 查询只做一次：一次刷新会解析十几到几十位演员，逐人全库扫描代价过高。
            /// </summary>
            private Dictionary<string, Person> EnsurePeopleByTmdbId()
            {
                if (this.peopleByTmdbId != null)
                {
                    return this.peopleByTmdbId;
                }

                var map = new Dictionary<string, Person>(StringComparer.Ordinal);
                var peopleQuery = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Person },
                    IsVirtualItem = false,
                    IsMissing = false,
                    Recursive = true,

                    // 映射只收录带 TMDb id 的人物，下推到数据库可避免加载全部人物；
                    // 判定只用 ProviderIds 列/导航，跳过 Data JSON 反序列化。
                    HasTmdbId = true,
                    SkipDeserialization = true,
                };

                var items = this.libraryManager.GetItemList(peopleQuery);
                if (items != null)
                {
                    foreach (var person in items.OfType<Person>())
                    {
                        var tmdbId = person.GetProviderId(MetadataProvider.Tmdb);
                        if (!string.IsNullOrWhiteSpace(tmdbId) && !map.ContainsKey(tmdbId))
                        {
                            map[tmdbId] = person;
                        }
                    }
                }

                this.peopleByTmdbId = map;
                return map;
            }
        }
    }
}
