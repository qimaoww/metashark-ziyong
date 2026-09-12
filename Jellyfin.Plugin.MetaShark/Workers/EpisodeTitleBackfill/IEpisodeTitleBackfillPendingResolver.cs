// <copyright file="IEpisodeTitleBackfillPendingResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill
{
    using System;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities.TV;

    public interface IEpisodeTitleBackfillPendingResolver
    {
        EpisodeTitleBackfillCandidate? TryClaimForUpdatedEpisode(Episode episode, string claimToken);

        Episode? ResolveCurrentEpisode(EpisodeTitleBackfillCandidate candidate);

        void MarkDeferredAttempt(EpisodeTitleBackfillCandidate candidate, DateTimeOffset nowUtc);

        /// <summary>
        /// 候选是否仍在待处理集合中（用于区分“已应用完成”和“本次未完成”）。
        /// </summary>
        bool IsPending(EpisodeTitleBackfillCandidate candidate);

        void ReleaseClaim(EpisodeTitleBackfillCandidate candidate, string claimToken);

        void Complete(EpisodeTitleBackfillCandidate candidate);

        void Expire(EpisodeTitleBackfillCandidate candidate);
    }
}
