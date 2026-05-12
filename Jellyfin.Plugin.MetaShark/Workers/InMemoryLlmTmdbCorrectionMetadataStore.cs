// <copyright file="InMemoryLlmTmdbCorrectionMetadataStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public sealed class InMemoryLlmTmdbCorrectionMetadataStore : ILlmTmdbCorrectionMetadataStore
    {
        private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(5);
        private readonly object syncRoot = new object();
        private readonly Dictionary<Guid, LlmTmdbCorrectionMetadataSnapshot> snapshotsByItemId = new Dictionary<Guid, LlmTmdbCorrectionMetadataSnapshot>();
        private readonly Dictionary<string, Guid> itemIdsByPath = new Dictionary<string, Guid>(GetPathComparer());

        public void Save(LlmTmdbCorrectionMetadataSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (snapshot.ItemId == Guid.Empty)
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                snapshot.QueuedAtUtc = snapshot.QueuedAtUtc == default ? DateTimeOffset.UtcNow : snapshot.QueuedAtUtc;
                snapshot.ExpiresAtUtc = snapshot.ExpiresAtUtc == default ? snapshot.QueuedAtUtc.Add(SnapshotLifetime) : snapshot.ExpiresAtUtc;
                this.Upsert(Clone(snapshot));
            }
        }

        public LlmTmdbCorrectionMetadataSnapshot? Peek(Guid itemId)
        {
            if (itemId == Guid.Empty)
            {
                return null;
            }

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                return this.snapshotsByItemId.TryGetValue(itemId, out var snapshot) ? Clone(snapshot) : null;
            }
        }

        public LlmTmdbCorrectionMetadataSnapshot? PeekByPath(string itemPath)
        {
            var normalizedPath = NormalizePath(itemPath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return null;
            }

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                if (!this.itemIdsByPath.TryGetValue(normalizedPath, out var itemId))
                {
                    return null;
                }

                return this.snapshotsByItemId.TryGetValue(itemId, out var snapshot) ? Clone(snapshot) : null;
            }
        }

        public LlmTmdbCorrectionMetadataSnapshot? TryClaim(Guid itemId, string itemPath, Guid currentItemId, string currentItemPath, string claimToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);

                var snapshot = this.FindSnapshot(itemId, itemPath);
                if (snapshot == null)
                {
                    return null;
                }

                if (snapshot.ItemId != currentItemId || !PathMatches(snapshot.ItemPath, currentItemPath))
                {
                    this.RemoveInternal(snapshot.ItemId, snapshot.ItemPath);
                    snapshot.ItemId = currentItemId == Guid.Empty ? snapshot.ItemId : currentItemId;
                    snapshot.ItemPath = string.IsNullOrWhiteSpace(currentItemPath) ? snapshot.ItemPath : currentItemPath;
                    this.snapshotsByItemId[snapshot.ItemId] = snapshot;
                    var normalizedUpdatedPath = NormalizePath(snapshot.ItemPath);
                    if (!string.IsNullOrEmpty(normalizedUpdatedPath))
                    {
                        this.itemIdsByPath[normalizedUpdatedPath] = snapshot.ItemId;
                    }
                }

                return Clone(snapshot);
            }
        }

        public void ReleaseClaim(Guid itemId, string itemPath, string claimToken)
        {
            _ = claimToken;
            this.Remove(itemId, itemPath);
        }

        public void Remove(Guid itemId, string itemPath)
        {
            var normalizedPath = NormalizePath(itemPath);

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);

                if (itemId != Guid.Empty && this.snapshotsByItemId.TryGetValue(itemId, out var snapshotById))
                {
                    this.RemoveInternal(snapshotById.ItemId, snapshotById.ItemPath);
                    return;
                }

                if (!string.IsNullOrEmpty(normalizedPath)
                    && this.itemIdsByPath.TryGetValue(normalizedPath, out var snapshotItemId)
                    && this.snapshotsByItemId.TryGetValue(snapshotItemId, out var snapshotByPath))
                {
                    this.RemoveInternal(snapshotByPath.ItemId, snapshotByPath.ItemPath);
                }
            }
        }

        private static LlmTmdbCorrectionMetadataSnapshot Clone(LlmTmdbCorrectionMetadataSnapshot snapshot)
        {
            var clone = new LlmTmdbCorrectionMetadataSnapshot
            {
                ItemId = snapshot.ItemId,
                ItemPath = snapshot.ItemPath,
                MediaType = snapshot.MediaType,
                TmdbId = snapshot.TmdbId,
                Name = snapshot.Name,
                OriginalTitle = snapshot.OriginalTitle,
                Overview = snapshot.Overview,
                ProductionYear = snapshot.ProductionYear,
                PremiereDate = snapshot.PremiereDate,
                QueuedAtUtc = snapshot.QueuedAtUtc,
                ExpiresAtUtc = snapshot.ExpiresAtUtc,
            };

            foreach (var providerId in snapshot.ProviderIds)
            {
                clone.ProviderIds[providerId.Key] = providerId.Value;
            }

            return clone;
        }

        private static string NormalizePath(string itemPath)
        {
            return string.IsNullOrWhiteSpace(itemPath) ? string.Empty : itemPath.Trim();
        }

        private static StringComparer GetPathComparer()
        {
            return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        }

        private static bool PathMatches(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private void Upsert(LlmTmdbCorrectionMetadataSnapshot snapshot)
        {
            if (this.snapshotsByItemId.TryGetValue(snapshot.ItemId, out var existingById))
            {
                this.RemoveInternal(existingById.ItemId, existingById.ItemPath);
            }

            var normalizedPath = NormalizePath(snapshot.ItemPath);
            if (!string.IsNullOrEmpty(normalizedPath)
                && this.itemIdsByPath.TryGetValue(normalizedPath, out var existingItemId)
                && this.snapshotsByItemId.TryGetValue(existingItemId, out var existingByPath)
                && existingByPath.ItemId != snapshot.ItemId)
            {
                this.RemoveInternal(existingByPath.ItemId, existingByPath.ItemPath);
            }

            this.snapshotsByItemId[snapshot.ItemId] = snapshot;
            if (!string.IsNullOrEmpty(normalizedPath))
            {
                this.itemIdsByPath[normalizedPath] = snapshot.ItemId;
            }
        }

        private LlmTmdbCorrectionMetadataSnapshot? FindSnapshot(Guid itemId, string itemPath)
        {
            if (itemId != Guid.Empty && this.snapshotsByItemId.TryGetValue(itemId, out var snapshotById))
            {
                return snapshotById;
            }

            var normalizedPath = NormalizePath(itemPath);
            if (!string.IsNullOrEmpty(normalizedPath)
                && this.itemIdsByPath.TryGetValue(normalizedPath, out var itemIdByPath)
                && this.snapshotsByItemId.TryGetValue(itemIdByPath, out var snapshotByPath))
            {
                return snapshotByPath;
            }

            return null;
        }

        private void RemoveExpiredEntries(DateTimeOffset nowUtc)
        {
            if (this.snapshotsByItemId.Count == 0)
            {
                return;
            }

            var expiredSnapshots = this.snapshotsByItemId.Values.Where(snapshot => snapshot.ExpiresAtUtc <= nowUtc).ToList();
            foreach (var snapshot in expiredSnapshots)
            {
                this.RemoveInternal(snapshot.ItemId, snapshot.ItemPath);
            }
        }

        private void RemoveInternal(Guid itemId, string itemPath)
        {
            if (itemId != Guid.Empty)
            {
                this.snapshotsByItemId.Remove(itemId);
            }

            var normalizedPath = NormalizePath(itemPath);
            if (!string.IsNullOrEmpty(normalizedPath))
            {
                this.itemIdsByPath.Remove(normalizedPath);
            }
        }
    }
}
