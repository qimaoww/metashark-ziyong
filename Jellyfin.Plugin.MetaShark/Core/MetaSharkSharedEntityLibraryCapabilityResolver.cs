// <copyright file="MetaSharkSharedEntityLibraryCapabilityResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Jellyfin.Data.Enums;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Entities;

    public sealed class MetaSharkSharedEntityLibraryCapabilityResolver
    {
        private const BindingFlags InstanceMemberBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly ILibraryManager libraryManager;
        private readonly MetaSharkOrdinaryItemLibraryCapabilityResolver ordinaryItemResolver;

        public MetaSharkSharedEntityLibraryCapabilityResolver(ILibraryManager libraryManager)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            this.libraryManager = libraryManager;
            this.ordinaryItemResolver = new MetaSharkOrdinaryItemLibraryCapabilityResolver(libraryManager);
        }

        public MetaSharkLibraryCapabilityDecision Resolve(BaseItem item, MetaSharkLibraryCapability capability)
        {
            ArgumentNullException.ThrowIfNull(item);

            return item switch
            {
                Person person => this.ResolveSharedEntity(this.GetRelatedMovieSeriesItems(person), capability),
                BoxSet boxSet => this.ResolveSharedEntity(this.GetAssociatedMovies(boxSet), capability),
                _ => MetaSharkLibraryCapabilityGate.Evaluate(
                    MetaSharkLibraryCapabilityGateInput.ForNoResolvedLibrary(
                        capability,
                        MetaSharkLibraryCapabilityContextKind.SharedEntity)),
            };
        }

        private static bool TryGetCurrentPeople(BaseItem item, ILibraryManager libraryManager, out IReadOnlyList<object?> people)
        {
            if (item.SupportsPeople)
            {
                var peopleFromLibraryManager = libraryManager.GetPeople(item);
                if (peopleFromLibraryManager != null)
                {
                    people = peopleFromLibraryManager.Cast<object?>().ToArray();
                    return true;
                }
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

        private static bool TryGetLinkedChildItemId(object? linkedChild, out Guid itemId)
        {
            itemId = Guid.Empty;

            switch (linkedChild)
            {
                case null:
                    return false;
                case LinkedChild linkedChildInfo when linkedChildInfo.ItemId.HasValue && linkedChildInfo.ItemId.Value != Guid.Empty:
                    itemId = linkedChildInfo.ItemId.Value;
                    return true;
                case BaseItem linkedItem when linkedItem.Id != Guid.Empty:
                    itemId = linkedItem.Id;
                    return true;
            }

            var itemIdProperty = linkedChild.GetType().GetProperty(nameof(LinkedChild.ItemId), InstanceMemberBindingFlags);
            if (itemIdProperty?.GetValue(linkedChild) is not Guid resolvedItemId || resolvedItemId == Guid.Empty)
            {
                return false;
            }

            itemId = resolvedItemId;
            return true;
        }

        private MetaSharkLibraryCapabilityDecision ResolveSharedEntity(IEnumerable<BaseItem> relatedItems, MetaSharkLibraryCapability capability)
        {
            var resolvedLibraries = relatedItems
                .SelectMany(relatedItem => this.ordinaryItemResolver.Resolve(relatedItem, capability).ResolvedLibraries)
                .Distinct()
                .ToArray();

            return resolvedLibraries.Length == 0
                ? MetaSharkLibraryCapabilityGate.Evaluate(MetaSharkLibraryCapabilityGateInput.ForSharedEntityUnresolved(capability))
                : MetaSharkLibraryCapabilityGate.Evaluate(MetaSharkLibraryCapabilityGateInput.ForSharedEntity(capability, resolvedLibraries));
        }

        private IEnumerable<BaseItem> GetAssociatedMovies(BoxSet boxSet)
        {
            ArgumentNullException.ThrowIfNull(boxSet);

            var linkedChildrenProperty = boxSet.GetType().GetProperty("LinkedChildren", InstanceMemberBindingFlags);
            if (linkedChildrenProperty?.GetValue(boxSet) is not IEnumerable linkedChildren)
            {
                return Enumerable.Empty<BaseItem>();
            }

            var movieIds = new HashSet<Guid>();
            var movies = new List<BaseItem>();

            foreach (var linkedChild in linkedChildren)
            {
                if (!TryGetLinkedChildItemId(linkedChild, out var itemId)
                    || !movieIds.Add(itemId)
                    || this.libraryManager.GetItemById(itemId) is not Movie movie)
                {
                    continue;
                }

                movies.Add(movie);
            }

            return movies;
        }

        private List<BaseItem> GetRelatedMovieSeriesItems(Person person)
        {
            ArgumentNullException.ThrowIfNull(person);

            var personTmdbId = person.GetProviderId(MetadataProvider.Tmdb);
            if (string.IsNullOrWhiteSpace(personTmdbId))
            {
                return new List<BaseItem>();
            }

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
            };

            var items = this.libraryManager.GetItemList(query) ?? Enumerable.Empty<BaseItem>();
            return items
                .Where(item => item is Movie or Series)
                .Where(item => this.CurrentItemContainsTmdbPersonId(item, personTmdbId))
                .ToList();
        }

        private bool CurrentItemContainsTmdbPersonId(BaseItem item, string personTmdbId)
        {
            if (!TryGetCurrentPeople(item, this.libraryManager, out var people))
            {
                return false;
            }

            foreach (var currentPerson in people)
            {
                if (TmdbAuthoritativePersonFingerprint.TryCreateFromCurrentPerson(currentPerson, out var fingerprint)
                    && fingerprint != null
                    && string.Equals(fingerprint.TmdbPersonId, personTmdbId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
