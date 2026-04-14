// <copyright file="InMemoryEpisodeTitleBackfillCandidateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Model;

    public sealed class InMemoryEpisodeTitleBackfillCandidateStore : IEpisodeTitleBackfillCandidateStore
    {
        private readonly object syncRoot = new object();
        private readonly Dictionary<Guid, EpisodeTitleBackfillCandidate> candidates = new Dictionary<Guid, EpisodeTitleBackfillCandidate>();

        public void Save(EpisodeTitleBackfillCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);

                if (candidate.ItemId == Guid.Empty)
                {
                    return;
                }

                this.candidates[candidate.ItemId] = Clone(candidate);
            }
        }

        public EpisodeTitleBackfillCandidate? Consume(Guid itemId, DateTimeOffset nowUtc)
        {
            if (itemId == Guid.Empty)
            {
                return null;
            }

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(nowUtc);

                if (!this.candidates.TryGetValue(itemId, out var candidate))
                {
                    return null;
                }

                this.candidates.Remove(itemId);
                return Clone(candidate);
            }
        }

        public void Remove(Guid itemId)
        {
            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);

                if (itemId == Guid.Empty)
                {
                    return;
                }

                this.candidates.Remove(itemId);
            }
        }

        private static EpisodeTitleBackfillCandidate Clone(EpisodeTitleBackfillCandidate candidate)
        {
            return new EpisodeTitleBackfillCandidate
            {
                ItemId = candidate.ItemId,
                OriginalTitleSnapshot = candidate.OriginalTitleSnapshot,
                CandidateTitle = candidate.CandidateTitle,
                CreatedAtUtc = candidate.CreatedAtUtc,
                ExpiresAtUtc = candidate.ExpiresAtUtc,
            };
        }

        private void RemoveExpiredEntries(DateTimeOffset nowUtc)
        {
            var expiredItemIds = new List<Guid>();

            foreach (var pair in this.candidates)
            {
                if (pair.Value.ExpiresAtUtc <= nowUtc)
                {
                    expiredItemIds.Add(pair.Key);
                }
            }

            foreach (var expiredItemId in expiredItemIds)
            {
                this.candidates.Remove(expiredItemId);
            }
        }
    }
}
