// <copyright file="InMemoryTmdbCorrectionRefreshIntentStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;

    public sealed class InMemoryTmdbCorrectionRefreshIntentStore : ITmdbCorrectionRefreshIntentStore
    {
        private static readonly TimeSpan IntentLifetime = TimeSpan.FromMinutes(5);
        private readonly object syncRoot = new object();
        private readonly Dictionary<Guid, RefreshIntentEntry> entriesByItemId = new Dictionary<Guid, RefreshIntentEntry>();
        private readonly Dictionary<string, Guid> itemIdsByPath = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        public static InMemoryTmdbCorrectionRefreshIntentStore Shared { get; } = new InMemoryTmdbCorrectionRefreshIntentStore();

        public void Save(Guid itemId, string? itemPath = null)
        {
            var normalizedPath = NormalizePath(itemPath);
            if (itemId == Guid.Empty && string.IsNullOrEmpty(normalizedPath))
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                this.Upsert(new RefreshIntentEntry(itemId, normalizedPath, DateTimeOffset.UtcNow.Add(IntentLifetime)));
            }
        }

        public bool HasPending(Guid itemId, string? itemPath = null)
        {
            var normalizedPath = NormalizePath(itemPath);

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                return this.TryResolveEntry(itemId, normalizedPath, out _);
            }
        }

        public bool TryConsume(Guid itemId, string? itemPath = null)
        {
            var normalizedPath = NormalizePath(itemPath);

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                if (!this.TryResolveEntry(itemId, normalizedPath, out var entry))
                {
                    return false;
                }

                this.RemoveInternal(entry.ItemId, entry.ItemPath);
                return true;
            }
        }

        private static string NormalizePath(string? itemPath)
        {
            return string.IsNullOrWhiteSpace(itemPath)
                ? string.Empty
                : itemPath.Trim().Replace('\\', '/');
        }

        private void Upsert(RefreshIntentEntry entry)
        {
            if (entry.ItemId != Guid.Empty && this.entriesByItemId.TryGetValue(entry.ItemId, out var existingById))
            {
                this.RemoveInternal(existingById.ItemId, existingById.ItemPath);
            }

            if (!string.IsNullOrEmpty(entry.ItemPath)
                && this.itemIdsByPath.TryGetValue(entry.ItemPath, out var existingItemId)
                && this.entriesByItemId.TryGetValue(existingItemId, out var existingByPath))
            {
                this.RemoveInternal(existingByPath.ItemId, existingByPath.ItemPath);
            }

            if (entry.ItemId != Guid.Empty)
            {
                this.entriesByItemId[entry.ItemId] = entry;
            }

            if (!string.IsNullOrEmpty(entry.ItemPath))
            {
                this.itemIdsByPath[entry.ItemPath] = entry.ItemId;
            }
        }

        private bool TryResolveEntry(Guid itemId, string normalizedPath, [NotNullWhen(true)] out RefreshIntentEntry? entry)
        {
            if (itemId != Guid.Empty && this.entriesByItemId.TryGetValue(itemId, out var entryById))
            {
                entry = entryById;
                return true;
            }

            if (!string.IsNullOrEmpty(normalizedPath)
                && this.itemIdsByPath.TryGetValue(normalizedPath, out var pathItemId)
                && this.entriesByItemId.TryGetValue(pathItemId, out var entryByPath))
            {
                entry = entryByPath;
                return true;
            }

            entry = null;
            return false;
        }

        private void RemoveExpiredEntries(DateTimeOffset now)
        {
            if (this.entriesByItemId.Count == 0)
            {
                return;
            }

            var expiredKeys = new List<Guid>();
            foreach (var pair in this.entriesByItemId)
            {
                if (pair.Value.ExpiresAtUtc <= now)
                {
                    expiredKeys.Add(pair.Key);
                }
            }

            foreach (var expiredKey in expiredKeys)
            {
                if (this.entriesByItemId.TryGetValue(expiredKey, out var entry))
                {
                    this.RemoveInternal(entry.ItemId, entry.ItemPath);
                }
            }
        }

        private void RemoveInternal(Guid itemId, string itemPath)
        {
            if (itemId != Guid.Empty)
            {
                this.entriesByItemId.Remove(itemId);
            }

            if (!string.IsNullOrEmpty(itemPath))
            {
                this.itemIdsByPath.Remove(itemPath);
            }
        }

        private sealed class RefreshIntentEntry
        {
            public RefreshIntentEntry(Guid itemId, string itemPath, DateTimeOffset expiresAtUtc)
            {
                this.ItemId = itemId;
                this.ItemPath = itemPath;
                this.ExpiresAtUtc = expiresAtUtc;
            }

            public Guid ItemId { get; }

            public string ItemPath { get; }

            public DateTimeOffset ExpiresAtUtc { get; }
        }
    }
}
