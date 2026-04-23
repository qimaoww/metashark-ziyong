// <copyright file="IEpisodeTitleBackfillCandidateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Model;

    public interface IEpisodeTitleBackfillCandidateStore
    {
        void Save(EpisodeTitleBackfillCandidate candidate);

        EpisodeTitleBackfillCandidate? Peek(Guid itemId);

        EpisodeTitleBackfillCandidate? PeekByPath(string itemPath);

        EpisodeTitleBackfillCandidate? TryClaim(Guid itemId, string itemPath, Guid currentItemId, string currentItemPath, string claimToken);

        IReadOnlyList<EpisodeTitleBackfillCandidate> GetDueDeferredRetries(DateTimeOffset nowUtc, int maxCount);

        void UpdateDeferredRetry(EpisodeTitleBackfillCandidate candidate);

        void ReleaseClaim(Guid itemId, string itemPath, string claimToken);

        void Remove(Guid itemId, string itemPath);
    }
}
