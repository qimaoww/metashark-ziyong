// <copyright file="IEpisodeOverviewCleanupPendingResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities.TV;

    public interface IEpisodeOverviewCleanupPendingResolver
    {
        EpisodeOverviewCleanupCandidate? TryClaimForUpdatedEpisode(Episode episode, string claimToken);

        Episode? ResolveCurrentEpisode(EpisodeOverviewCleanupCandidate candidate);

        void MarkDeferredAttempt(EpisodeOverviewCleanupCandidate candidate, DateTimeOffset nowUtc);

        void ReleaseClaim(EpisodeOverviewCleanupCandidate candidate, string claimToken);

        void Complete(EpisodeOverviewCleanupCandidate candidate);

        void Expire(EpisodeOverviewCleanupCandidate candidate);
    }
}
