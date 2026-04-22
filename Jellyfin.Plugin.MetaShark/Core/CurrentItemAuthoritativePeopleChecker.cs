// <copyright file="CurrentItemAuthoritativePeopleChecker.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using MediaBrowser.Controller.Entities;

    public enum CurrentItemAuthoritativePeopleStatus
    {
        NonAuthoritative = 0,
        Authoritative = 1,
        AuthoritativeEmpty = 2,
    }

    public static class CurrentItemAuthoritativePeopleChecker
    {
        public static CurrentItemAuthoritativePeopleStatus Check(BaseItem? item, PeopleRefreshState? state)
        {
            return Check(item, state?.AuthoritativePeopleSnapshot);
        }

        public static CurrentItemAuthoritativePeopleStatus Check(BaseItem? item, TmdbAuthoritativePeopleSnapshot? authoritativeSnapshot)
        {
            if (item == null
                || authoritativeSnapshot == null
                || !authoritativeSnapshot.MatchesIdentity(item)
                || !TmdbAuthoritativePeopleSnapshot.TryCreateFromCurrentItem(item, out var currentSnapshot)
                || currentSnapshot == null
                || !authoritativeSnapshot.SetEquals(currentSnapshot))
            {
                return CurrentItemAuthoritativePeopleStatus.NonAuthoritative;
            }

            return authoritativeSnapshot.IsAuthoritativeEmpty
                ? CurrentItemAuthoritativePeopleStatus.AuthoritativeEmpty
                : CurrentItemAuthoritativePeopleStatus.Authoritative;
        }

        public static bool IsAuthoritative(BaseItem? item, PeopleRefreshState? state)
        {
            return Check(item, state) != CurrentItemAuthoritativePeopleStatus.NonAuthoritative;
        }

        public static bool IsAuthoritative(BaseItem? item, TmdbAuthoritativePeopleSnapshot? authoritativeSnapshot)
        {
            return Check(item, authoritativeSnapshot) != CurrentItemAuthoritativePeopleStatus.NonAuthoritative;
        }

        public static bool IsAuthoritativeEmpty(BaseItem? item, PeopleRefreshState? state)
        {
            return Check(item, state) == CurrentItemAuthoritativePeopleStatus.AuthoritativeEmpty;
        }

        public static bool IsAuthoritativeEmpty(BaseItem? item, TmdbAuthoritativePeopleSnapshot? authoritativeSnapshot)
        {
            return Check(item, authoritativeSnapshot) == CurrentItemAuthoritativePeopleStatus.AuthoritativeEmpty;
        }
    }
}
