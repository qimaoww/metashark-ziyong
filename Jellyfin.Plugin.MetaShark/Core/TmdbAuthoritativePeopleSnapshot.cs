// <copyright file="TmdbAuthoritativePeopleSnapshot.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Model.Entities;

    public sealed class TmdbAuthoritativePeopleSnapshot
    {
        private const BindingFlags InstanceMemberBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public string ItemType { get; set; } = string.Empty;

        public string TmdbId { get; set; } = string.Empty;

        public List<TmdbAuthoritativePersonFingerprint> People { get; set; } = new List<TmdbAuthoritativePersonFingerprint>();

        public bool IsAuthoritativeEmpty => this.People.Count == 0;

        public static TmdbAuthoritativePeopleSnapshot Create(string itemType, string tmdbId, IEnumerable<PersonInfo> people)
        {
            ArgumentNullException.ThrowIfNull(people);

            return Create(itemType, tmdbId, people.Select(TmdbAuthoritativePersonFingerprint.FromPersonInfo));
        }

        public static TmdbAuthoritativePeopleSnapshot Create(string itemType, string tmdbId, IEnumerable<TmdbAuthoritativePersonFingerprint> people)
        {
            ArgumentNullException.ThrowIfNull(people);

            var snapshot = new TmdbAuthoritativePeopleSnapshot
            {
                ItemType = NormalizeItemType(itemType),
                TmdbId = NormalizeTmdbId(tmdbId),
            };

            foreach (var fingerprint in NormalizeFingerprints(people))
            {
                snapshot.People.Add(fingerprint);
            }

            return snapshot;
        }

        public static bool TryCreateFromCurrentItem(BaseItem? item, out TmdbAuthoritativePeopleSnapshot? snapshot)
        {
            snapshot = null;

            if (item == null || !PeopleRefreshState.TryGetIdentity(item, out _, out var itemType, out var tmdbId))
            {
                return false;
            }

            if (!TryGetCurrentPeople(item, out var people) || !TryCreateFingerprintsFromCurrentPeople(people, out var fingerprints))
            {
                return false;
            }

            snapshot = Create(itemType, tmdbId, fingerprints);
            return true;
        }

        public bool MatchesIdentity(BaseItem? item)
        {
            return PeopleRefreshState.TryGetIdentity(item, out _, out var itemType, out var tmdbId)
                && string.Equals(this.ItemType, itemType, StringComparison.Ordinal)
                && string.Equals(this.TmdbId, tmdbId, StringComparison.Ordinal);
        }

        public bool SetEquals(TmdbAuthoritativePeopleSnapshot? other)
        {
            if (other == null)
            {
                return false;
            }

            return string.Equals(this.ItemType, other.ItemType, StringComparison.Ordinal)
                && string.Equals(this.TmdbId, other.TmdbId, StringComparison.Ordinal)
                && this.CreateFingerprintKeySet().SetEquals(other.CreateFingerprintKeySet());
        }

        public TmdbAuthoritativePeopleSnapshot Clone()
        {
            return new TmdbAuthoritativePeopleSnapshot
            {
                ItemType = this.ItemType,
                TmdbId = this.TmdbId,
                People = this.People.Select(static fingerprint => fingerprint.Clone()).ToList(),
            };
        }

        private static string NormalizeItemType(string itemType)
        {
            if (string.Equals(itemType, nameof(Movie), StringComparison.Ordinal)
                || string.Equals(itemType, nameof(Series), StringComparison.Ordinal))
            {
                return itemType;
            }

            throw new ArgumentOutOfRangeException(nameof(itemType), itemType, "Only Movie or Series is supported.");
        }

        private static string NormalizeTmdbId(string tmdbId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tmdbId);
            return tmdbId.Trim();
        }

        private static IEnumerable<TmdbAuthoritativePersonFingerprint> NormalizeFingerprints(IEnumerable<TmdbAuthoritativePersonFingerprint> people)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fingerprint in people)
            {
                ArgumentNullException.ThrowIfNull(fingerprint);

                var normalized = TmdbAuthoritativePersonFingerprint.Create(fingerprint.TmdbPersonId, fingerprint.PersonType, fingerprint.Role);
                if (seen.Add(normalized.ToKey()))
                {
                    yield return normalized;
                }
            }
        }

        private static bool TryGetCurrentPeople(BaseItem item, out IReadOnlyList<object?> people)
        {
            var currentType = item.GetType();

            while (currentType != null)
            {
                var getPeopleMethod = currentType.GetMethod("GetPeople", InstanceMemberBindingFlags, null, Type.EmptyTypes, null);
                if (getPeopleMethod?.Invoke(item, null) is IEnumerable peopleFromMethod)
                {
                    people = peopleFromMethod.Cast<object?>().ToArray();
                    return true;
                }

                var peopleProperty = currentType.GetProperty("People", InstanceMemberBindingFlags);
                if (peopleProperty?.GetValue(item) is IEnumerable peopleFromProperty)
                {
                    people = peopleFromProperty.Cast<object?>().ToArray();
                    return true;
                }

                currentType = currentType.BaseType;
            }

            people = Array.Empty<object?>();
            return false;
        }

        private static bool TryCreateFingerprintsFromCurrentPeople(IReadOnlyList<object?> people, out IReadOnlyList<TmdbAuthoritativePersonFingerprint> fingerprints)
        {
            var result = new List<TmdbAuthoritativePersonFingerprint>(people.Count);

            foreach (var person in people)
            {
                if (!TmdbAuthoritativePersonFingerprint.TryCreateFromCurrentPerson(person, out var fingerprint) || fingerprint == null)
                {
                    fingerprints = Array.Empty<TmdbAuthoritativePersonFingerprint>();
                    return false;
                }

                result.Add(fingerprint);
            }

            fingerprints = result;
            return true;
        }

        private HashSet<string> CreateFingerprintKeySet()
        {
            return new HashSet<string>(this.People.Select(static fingerprint => fingerprint.ToKey()), StringComparer.Ordinal);
        }
    }

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
