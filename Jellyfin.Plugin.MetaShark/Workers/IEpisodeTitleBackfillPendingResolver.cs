// <copyright file="IEpisodeTitleBackfillPendingResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities.TV;

    public interface IEpisodeTitleBackfillPendingResolver
    {
        EpisodeTitleBackfillCandidate? TryClaimForUpdatedEpisode(Episode episode, string claimToken);

        Episode? ResolveCurrentEpisode(EpisodeTitleBackfillCandidate candidate);

        void MarkDeferredAttempt(EpisodeTitleBackfillCandidate candidate, DateTimeOffset nowUtc);

        void ReleaseClaim(EpisodeTitleBackfillCandidate candidate, string claimToken);

        void Complete(EpisodeTitleBackfillCandidate candidate);

        void Expire(EpisodeTitleBackfillCandidate candidate);
    }
}
