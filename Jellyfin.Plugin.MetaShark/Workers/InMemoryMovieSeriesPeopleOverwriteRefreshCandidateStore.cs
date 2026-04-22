// <copyright file="InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;

    public sealed class InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore : IMovieSeriesPeopleOverwriteRefreshCandidateStore
    {
        private readonly object syncRoot = new object();
        private readonly Dictionary<Guid, MovieSeriesPeopleOverwriteRefreshCandidate> candidatesByItemId = new Dictionary<Guid, MovieSeriesPeopleOverwriteRefreshCandidate>();
        private readonly Dictionary<string, Guid> itemIdsByPath = new Dictionary<string, Guid>(GetPathComparer());

        public static InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore Shared { get; } = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();

        public void Save(MovieSeriesPeopleOverwriteRefreshCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            if (candidate.ItemId == Guid.Empty)
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.Upsert(Clone(candidate));
            }
        }

        public MovieSeriesPeopleOverwriteRefreshCandidate? Peek(Guid itemId)
        {
            if (itemId == Guid.Empty)
            {
                return null;
            }

            lock (this.syncRoot)
            {
                return this.candidatesByItemId.TryGetValue(itemId, out var candidate)
                    ? Clone(candidate)
                    : null;
            }
        }

        public MovieSeriesPeopleOverwriteRefreshCandidate? Consume(Guid itemId, string itemPath)
        {
            var normalizedItemPath = NormalizePath(itemPath);

            lock (this.syncRoot)
            {
                if (itemId != Guid.Empty && this.candidatesByItemId.TryGetValue(itemId, out var candidateById))
                {
                    this.RemoveInternal(candidateById.ItemId, candidateById.ItemPath);
                    return Clone(candidateById);
                }

                if (!string.IsNullOrEmpty(normalizedItemPath)
                    && this.itemIdsByPath.TryGetValue(normalizedItemPath, out var candidateItemId)
                    && this.candidatesByItemId.TryGetValue(candidateItemId, out var candidateByPath))
                {
                    this.RemoveInternal(candidateByPath.ItemId, candidateByPath.ItemPath);
                    return Clone(candidateByPath);
                }

                return null;
            }
        }

        private static MovieSeriesPeopleOverwriteRefreshCandidate Clone(MovieSeriesPeopleOverwriteRefreshCandidate candidate)
        {
            return new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = candidate.ItemId,
                ItemPath = candidate.ItemPath,
                ExpectedPeopleCount = candidate.ExpectedPeopleCount,
                AuthoritativePeopleSnapshot = candidate.AuthoritativePeopleSnapshot?.Clone(),
                OverwriteQueued = candidate.OverwriteQueued,
            };
        }

        private static string NormalizePath(string itemPath)
        {
            return itemPath ?? string.Empty;
        }

        private static StringComparer GetPathComparer()
        {
            return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        }

        private static bool PathMatches(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private void RemoveInternal(Guid itemId, string itemPath)
        {
            this.candidatesByItemId.Remove(itemId);

            var normalizedItemPath = NormalizePath(itemPath);
            if (!string.IsNullOrEmpty(normalizedItemPath))
            {
                this.itemIdsByPath.Remove(normalizedItemPath);
            }
        }

        private void Upsert(MovieSeriesPeopleOverwriteRefreshCandidate candidate)
        {
            if (this.candidatesByItemId.TryGetValue(candidate.ItemId, out var existingCandidateById))
            {
                this.RemoveInternal(existingCandidateById.ItemId, existingCandidateById.ItemPath);
            }

            var normalizedItemPath = NormalizePath(candidate.ItemPath);
            if (!string.IsNullOrEmpty(normalizedItemPath)
                && this.itemIdsByPath.TryGetValue(normalizedItemPath, out var existingItemId)
                && this.candidatesByItemId.TryGetValue(existingItemId, out var existingCandidateByPath)
                && existingItemId != candidate.ItemId)
            {
                this.RemoveInternal(existingCandidateByPath.ItemId, existingCandidateByPath.ItemPath);
            }

            this.candidatesByItemId[candidate.ItemId] = candidate;
            if (!string.IsNullOrEmpty(normalizedItemPath))
            {
                this.itemIdsByPath[normalizedItemPath] = candidate.ItemId;
            }
        }
    }
}
