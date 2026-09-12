// <copyright file="TmdbAuthoritativePersonFingerprint.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Entities;

    public sealed class TmdbAuthoritativePersonFingerprint
    {
        private const BindingFlags InstanceMemberBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public string TmdbPersonId { get; set; } = string.Empty;

        public string PersonType { get; set; } = string.Empty;

        public string Role { get; set; } = string.Empty;

        public static TmdbAuthoritativePersonFingerprint Create(string tmdbPersonId, string personType, string role)
        {
            return new TmdbAuthoritativePersonFingerprint
            {
                TmdbPersonId = NormalizeTmdbPersonId(tmdbPersonId),
                PersonType = NormalizePersonType(personType),
                Role = NormalizeRole(role),
            };
        }

        public static TmdbAuthoritativePersonFingerprint FromPersonInfo(PersonInfo person)
        {
            ArgumentNullException.ThrowIfNull(person);

            if (!TryReadTmdbId(person.ProviderIds as IDictionary<string, string>, out var tmdbPersonId))
            {
                throw new ArgumentException("TMDb person id is required.", nameof(person));
            }

            return Create(tmdbPersonId, person.Type.ToString(), person.Role ?? string.Empty);
        }

        /// <summary>
        /// Jellyfin 12 的 GetPeople/GetPeopleByItems 投影不再包含 ProviderIds，
        /// 需要经 Person 实体读取 TMDb id；实体缺失时回退到传入对象（测试替身等场景）。
        /// </summary>
        internal static object? ResolvePersonForProviderIds(
            object? person,
            ILibraryManager? libraryManager,
            IDictionary<string, Dictionary<string, string>?>? providerIdsCache = null)
        {
            if (person is not PersonInfo personInfo || libraryManager == null)
            {
                return person;
            }

            // Jellyfin 12 的 GetPeople/GetPeopleByItems 投影已经带上了 ProviderIds 时无需再查。
            if (personInfo.ProviderIds is { Count: > 0 })
            {
                return personInfo;
            }

            var cacheKey = string.IsNullOrWhiteSpace(personInfo.Name) ? null : personInfo.Name;
            Dictionary<string, string>? providerIds;
            if (cacheKey != null && providerIdsCache != null && providerIdsCache.TryGetValue(cacheKey, out var cachedProviderIds))
            {
                providerIds = cachedProviderIds;
            }
            else
            {
                // Jellyfin 12 的 PersonInfo.Id 是 Peoples 实体 Id，并不是 BaseItems 里 Person 条目的 Id，
                // 用 GetItemById 查不到；必须按人物名取 Person 条目才能读到 ProviderIds。
                providerIds = ResolvePersonEntity(personInfo, libraryManager)?.ProviderIds;

                if (cacheKey != null && providerIdsCache != null)
                {
                    providerIdsCache[cacheKey] = providerIds;
                }
            }

            // Person 条目只有 ProviderIds，实体上没有 Type/Role；PersonInfo 反之。
            // 因此把条目的 ProviderIds 合并回 PersonInfo 后再构造指纹。
            return MergeProviderIds(personInfo, providerIds) ? personInfo : person;
        }

        /// <summary>
        /// 按人物名解析 Jellyfin 的 Person 条目，回退到 PersonInfo.Id 恰好为条目 Id 的实现（以及测试替身）。
        /// </summary>
        internal static Person? ResolvePersonEntity(PersonInfo personInfo, ILibraryManager? libraryManager)
        {
            if (personInfo == null || libraryManager == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(personInfo.Name)
                && libraryManager.GetPerson(personInfo.Name) is Person personByName)
            {
                return personByName;
            }

            if (personInfo.Id != Guid.Empty && libraryManager.GetItemById(personInfo.Id) is Person entity)
            {
                return entity;
            }

            return null;
        }

        /// <summary>
        /// 把 Person 条目的 ProviderIds 合并进 PersonInfo，保留 PersonInfo 上的 Type/Role。
        /// </summary>
        internal static bool MergeProviderIds(PersonInfo? personInfo, IReadOnlyDictionary<string, string>? providerIds)
        {
            if (personInfo == null || providerIds is not { Count: > 0 })
            {
                return false;
            }

            personInfo.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var merged = false;
            foreach (var pair in providerIds)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    personInfo.ProviderIds[pair.Key] = pair.Value;
                    merged = true;
                }
            }

            return merged;
        }

        public static bool TryCreateFromCurrentPerson(object? person, out TmdbAuthoritativePersonFingerprint? fingerprint)
        {
            fingerprint = null;

            if (person == null
                || !TryGetTmdbPersonId(person, out var tmdbPersonId)
                || !TryGetPersonType(person, out var personType)
                || !TryGetRole(person, out var role))
            {
                return false;
            }

            fingerprint = Create(tmdbPersonId, personType, role);
            return true;
        }

        public TmdbAuthoritativePersonFingerprint Clone()
        {
            return Create(this.TmdbPersonId, this.PersonType, this.Role);
        }

        internal string ToKey()
        {
            return string.Join("|", this.TmdbPersonId, this.PersonType, this.Role);
        }

        private static string NormalizeTmdbPersonId(string tmdbPersonId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tmdbPersonId);
            return tmdbPersonId.Trim();
        }

        private static string NormalizePersonType(string personType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(personType);
            return personType.Trim();
        }

        private static string NormalizeRole(string role)
        {
            return role?.Trim() ?? string.Empty;
        }

        private static bool TryGetTmdbPersonId(object person, out string tmdbPersonId)
        {
            tmdbPersonId = string.Empty;

            if (person is PersonInfo personInfo)
            {
                return TryReadTmdbId(personInfo.ProviderIds as IDictionary<string, string>, out tmdbPersonId);
            }

            var providerIdsProperty = person.GetType().GetProperty("ProviderIds", InstanceMemberBindingFlags);
            var providerIds = providerIdsProperty?.GetValue(person);

            return providerIds switch
            {
                IDictionary<string, string> genericDictionary => TryReadTmdbId(genericDictionary, out tmdbPersonId),
                IDictionary dictionary => TryReadTmdbIdFromDictionary(dictionary, out tmdbPersonId),
                _ => false,
            };
        }

        private static bool TryGetPersonType(object person, out string personType)
        {
            personType = string.Empty;

            if (person is PersonInfo personInfo)
            {
                personType = NormalizePersonType(personInfo.Type.ToString());
                return true;
            }

            var typeProperty = person.GetType().GetProperty("Type", InstanceMemberBindingFlags);
            var typeValue = typeProperty?.GetValue(person)?.ToString();
            if (string.IsNullOrWhiteSpace(typeValue))
            {
                return false;
            }

            personType = NormalizePersonType(typeValue);
            return true;
        }

        private static bool TryGetRole(object person, out string role)
        {
            role = string.Empty;

            if (person is PersonInfo personInfo)
            {
                role = NormalizeRole(personInfo.Role ?? string.Empty);
                return true;
            }

            var roleProperty = person.GetType().GetProperty("Role", InstanceMemberBindingFlags);
            if (roleProperty == null)
            {
                return false;
            }

            role = NormalizeRole(roleProperty.GetValue(person)?.ToString() ?? string.Empty);
            return true;
        }

        private static bool TryReadTmdbId(object? providerIds, out string tmdbPersonId)
        {
            tmdbPersonId = string.Empty;

            return providerIds switch
            {
                IReadOnlyDictionary<string, string> readOnlyDictionary => TryReadTmdbIdFromReadOnlyDictionary(readOnlyDictionary, out tmdbPersonId),
                IDictionary<string, string> genericDictionary => TryReadTmdbIdFromGenericDictionary(genericDictionary, out tmdbPersonId),
                IDictionary dictionary => TryReadTmdbIdFromDictionary(dictionary, out tmdbPersonId),
                _ => false,
            };
        }

        private static bool TryReadTmdbIdFromReadOnlyDictionary(IReadOnlyDictionary<string, string> providerIds, out string tmdbPersonId)
        {
            tmdbPersonId = string.Empty;

            return providerIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var providerId)
                && !string.IsNullOrWhiteSpace(providerId)
                && TryNormalizeProviderId(providerId, out tmdbPersonId);
        }

        private static bool TryReadTmdbIdFromGenericDictionary(IDictionary<string, string> providerIds, out string tmdbPersonId)
        {
            tmdbPersonId = string.Empty;

            return providerIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var providerId)
                && !string.IsNullOrWhiteSpace(providerId)
                && TryNormalizeProviderId(providerId, out tmdbPersonId);
        }

        private static bool TryReadTmdbIdFromDictionary(IDictionary providerIds, out string tmdbPersonId)
        {
            tmdbPersonId = string.Empty;
            var key = MetadataProvider.Tmdb.ToString();

            return providerIds.Contains(key)
                && providerIds[key] is string providerId
                && !string.IsNullOrWhiteSpace(providerId)
                && TryNormalizeProviderId(providerId, out tmdbPersonId);
        }

        private static bool TryNormalizeProviderId(string providerId, out string normalizedProviderId)
        {
            normalizedProviderId = providerId.Trim();
            return normalizedProviderId.Length > 0;
        }
    }
}
