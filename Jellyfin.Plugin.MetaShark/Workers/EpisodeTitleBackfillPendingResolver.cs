// <copyright file="EpisodeTitleBackfillPendingResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;

    public sealed class EpisodeTitleBackfillPendingResolver : IEpisodeTitleBackfillPendingResolver
    {
        private readonly IEpisodeTitleBackfillCandidateStore candidateStore;
        private readonly ILibraryManager? libraryManager;

        public EpisodeTitleBackfillPendingResolver(IEpisodeTitleBackfillCandidateStore candidateStore)
            : this(candidateStore, null)
        {
        }

        public EpisodeTitleBackfillPendingResolver(IEpisodeTitleBackfillCandidateStore candidateStore, ILibraryManager? libraryManager)
        {
            this.candidateStore = candidateStore;
            this.libraryManager = libraryManager;
        }

        public EpisodeTitleBackfillCandidate? TryClaimForUpdatedEpisode(Episode episode, string claimToken)
        {
            ArgumentNullException.ThrowIfNull(episode);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            if (episode.Id == Guid.Empty)
            {
                return null;
            }

            return this.candidateStore.TryClaim(episode.Id, episode.Path ?? string.Empty, episode.Id, episode.Path ?? string.Empty, claimToken);
        }

        public Episode? ResolveCurrentEpisode(EpisodeTitleBackfillCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            if (this.libraryManager == null || string.IsNullOrWhiteSpace(candidate.ItemPath))
            {
                return null;
            }

            return this.libraryManager.FindByPath(candidate.ItemPath, false) as Episode;
        }

        public void MarkDeferredAttempt(EpisodeTitleBackfillCandidate candidate, DateTimeOffset nowUtc)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            candidate.AttemptCount += 1;
            candidate.NextAttemptAtUtc = nowUtc.AddSeconds(10);
            this.candidateStore.UpdateDeferredRetry(candidate);
        }

        public void ReleaseClaim(EpisodeTitleBackfillCandidate candidate, string claimToken)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
            this.candidateStore.ReleaseClaim(candidate.ItemId, candidate.ItemPath, claimToken);
        }

        public void Complete(EpisodeTitleBackfillCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            this.candidateStore.Remove(candidate.ItemId, candidate.ItemPath);
        }

        public void Expire(EpisodeTitleBackfillCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            this.candidateStore.Remove(candidate.ItemId, candidate.ItemPath);
        }
    }
}
