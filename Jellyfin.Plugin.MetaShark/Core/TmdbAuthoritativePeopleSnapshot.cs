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
    using System.Text.Json.Serialization;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Model.Entities;

    public sealed class TmdbAuthoritativePeopleSnapshot
    {
        private const BindingFlags InstanceMemberBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public string ItemType { get; set; } = string.Empty;

        public string TmdbId { get; set; } = string.Empty;

        [JsonInclude]
        public IReadOnlyList<TmdbAuthoritativePersonFingerprint> People { get; private set; } = Array.Empty<TmdbAuthoritativePersonFingerprint>();

        public bool IsAuthoritativeEmpty => this.People.Count == 0;

        public static TmdbAuthoritativePeopleSnapshot Create(string itemType, string tmdbId, IEnumerable<PersonInfo> people)
        {
            ArgumentNullException.ThrowIfNull(people);

            return Create(itemType, tmdbId, people.Select(TmdbAuthoritativePersonFingerprint.FromPersonInfo));
        }

        public static TmdbAuthoritativePeopleSnapshot Create(string itemType, string tmdbId, IEnumerable<TmdbAuthoritativePersonFingerprint> people)
        {
            ArgumentNullException.ThrowIfNull(people);

            return new TmdbAuthoritativePeopleSnapshot
            {
                ItemType = NormalizeItemType(itemType),
                TmdbId = NormalizeTmdbId(tmdbId),
                People = NormalizeFingerprints(people).ToArray(),
            };
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
                People = this.People.Select(static fingerprint => fingerprint.Clone()).ToArray(),
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
            var libraryManager = BaseItem.LibraryManager;
            if (item.SupportsPeople && libraryManager != null)
            {
                var peopleFromLibraryManager = libraryManager.GetPeople(item);
                people = peopleFromLibraryManager.Cast<object?>().ToArray();
                return true;
            }

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
}
