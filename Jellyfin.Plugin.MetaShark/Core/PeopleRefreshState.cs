// <copyright file="PeopleRefreshState.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Model.Entities;

    public class PeopleRefreshState
    {
        public const string CurrentVersion = "tmdb-people-strict-zh-cn-v2";

        public Guid ItemId { get; set; }

        public string ItemType { get; set; } = string.Empty;

        public string TmdbId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public DateTimeOffset UpdatedAtUtc { get; set; }

        public static bool RequiresBackfill(BaseItem? item, PeopleRefreshState? state)
        {
            return IsInScope(item) && !HasCurrentState(item, state);
        }

        public static bool HasCurrentState(BaseItem? item, PeopleRefreshState? state)
        {
            if (!TryGetIdentity(item, out var itemId, out var itemType, out var tmdbId))
            {
                return false;
            }

            return state != null
                && state.ItemId == itemId
                && string.Equals(state.ItemType, itemType, StringComparison.Ordinal)
                && string.Equals(state.TmdbId, tmdbId, StringComparison.Ordinal)
                && string.Equals(state.Version, CurrentVersion, StringComparison.Ordinal);
        }

        public static bool IsMissing(BaseItem? item, PeopleRefreshState? state)
        {
            return IsInScope(item) && state == null;
        }

        public static bool IsStale(BaseItem? item, PeopleRefreshState? state)
        {
            return IsInScope(item) && state != null && !HasCurrentState(item, state);
        }

        public static bool IsInScope(BaseItem? item)
        {
            return TryGetIdentity(item, out _, out _, out _);
        }

        public static bool TryCreateCurrent(BaseItem? item, out PeopleRefreshState? state)
        {
            if (!TryGetIdentity(item, out var itemId, out var itemType, out var tmdbId))
            {
                state = null;
                return false;
            }

            state = new PeopleRefreshState
            {
                ItemId = itemId,
                ItemType = itemType,
                TmdbId = tmdbId,
                Version = CurrentVersion,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return true;
        }

        private static bool TryGetIdentity(BaseItem? item, out Guid itemId, out string itemType, out string tmdbId)
        {
            itemId = item?.Id ?? Guid.Empty;
            itemType = string.Empty;
            tmdbId = string.Empty;

            if (item is not Movie and not Series)
            {
                return false;
            }

            if (itemId == Guid.Empty)
            {
                return false;
            }

            tmdbId = item.GetProviderId(MetadataProvider.Tmdb) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                tmdbId = string.Empty;
                return false;
            }

            itemType = item is Movie ? nameof(Movie) : nameof(Series);
            return true;
        }
    }
}
