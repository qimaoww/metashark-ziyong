// <copyright file="IEpisodeOverviewCleanupCandidateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Model;

    public interface IEpisodeOverviewCleanupCandidateStore
    {
        void Save(EpisodeOverviewCleanupCandidate candidate);

        EpisodeOverviewCleanupCandidate? Peek(Guid itemId);

        EpisodeOverviewCleanupCandidate? PeekByPath(string itemPath);

        EpisodeOverviewCleanupCandidate? TryClaim(Guid itemId, string itemPath, Guid currentItemId, string currentItemPath, string claimToken);

        IReadOnlyList<EpisodeOverviewCleanupCandidate> GetDueDeferredRetries(DateTimeOffset nowUtc, int maxCount);

        void UpdateDeferredRetry(EpisodeOverviewCleanupCandidate candidate);

        void ReleaseClaim(Guid itemId, string itemPath, string claimToken);

        void Remove(Guid itemId, string itemPath);
    }
}
