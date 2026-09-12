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
            if (!TmdbAuthoritativePeopleSnapshot.TryCreateFromCurrentItem(item, out var currentSnapshot))
            {
                return CurrentItemAuthoritativePeopleStatus.NonAuthoritative;
            }

            return Check(item, authoritativeSnapshot, currentSnapshot);
        }

        /// <summary>
        /// 基于调用方已经取好的当前条目人物快照判定，避免同一条目事件里重复查询人物。
        /// </summary>
        /// <param name="item">当前条目。</param>
        /// <param name="authoritativeSnapshot">已保存的权威人物快照。</param>
        /// <param name="currentSnapshot">当前条目的人物快照，取不到时传 null。</param>
        /// <returns>权威性判定结果。</returns>
        public static CurrentItemAuthoritativePeopleStatus Check(
            BaseItem? item,
            TmdbAuthoritativePeopleSnapshot? authoritativeSnapshot,
            TmdbAuthoritativePeopleSnapshot? currentSnapshot)
        {
            if (item == null
                || authoritativeSnapshot == null
                || !authoritativeSnapshot.MatchesIdentity(item)
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
