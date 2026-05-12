// <copyright file="InMemoryTmdbCorrectionRefreshIntentStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;

    [SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1201:Elements should appear in the correct order", Justification = "Private nested intent key types stay near the store fields they support.")]
    [SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before non-static members", Justification = "Public interface methods stay before private helpers.")]
    public sealed class InMemoryTmdbCorrectionRefreshIntentStore : ITmdbCorrectionRefreshIntentStore
    {
        private static readonly TimeSpan IntentLifetime = TimeSpan.FromMinutes(5);

        private readonly object syncRoot = new object();
        private readonly Dictionary<RefreshIntentKey, RefreshIntentEntry> entriesByKey = new Dictionary<RefreshIntentKey, RefreshIntentEntry>();
        private readonly Dictionary<RefreshIntentPathKey, RefreshIntentKey> itemKeysByPath = new Dictionary<RefreshIntentPathKey, RefreshIntentKey>();

        public static InMemoryTmdbCorrectionRefreshIntentStore Shared { get; } = new InMemoryTmdbCorrectionRefreshIntentStore();

        private enum RefreshIntentKind
        {
            SearchMissingMetadata,
            OverwriteMetadata,
        }

        public void Save(Guid itemId, string? itemPath = null)
        {
            this.SaveSearchMissing(itemId, itemPath);
        }

        public void SaveSearchMissing(Guid itemId, string? itemPath = null)
        {
            this.SaveCore(RefreshIntentKind.SearchMissingMetadata, itemId, itemPath);
        }

        public void SaveOverwrite(Guid itemId, string? itemPath = null)
        {
            this.SaveCore(RefreshIntentKind.OverwriteMetadata, itemId, itemPath);
        }

        public bool HasPending(Guid itemId, string? itemPath = null)
        {
            return this.HasPendingSearchMissing(itemId, itemPath);
        }

        public bool HasPendingSearchMissing(Guid itemId, string? itemPath = null)
        {
            return this.HasPendingCore(RefreshIntentKind.SearchMissingMetadata, itemId, itemPath);
        }

        public bool HasPendingOverwrite(Guid itemId, string? itemPath = null)
        {
            return this.HasPendingCore(RefreshIntentKind.OverwriteMetadata, itemId, itemPath);
        }

        public bool TryConsume(Guid itemId, string? itemPath = null)
        {
            return this.TryConsumeSearchMissing(itemId, itemPath);
        }

        public bool TryConsumeSearchMissing(Guid itemId, string? itemPath = null)
        {
            return this.TryConsumeCore(RefreshIntentKind.SearchMissingMetadata, itemId, itemPath);
        }

        public bool TryConsumeOverwrite(Guid itemId, string? itemPath = null)
        {
            return this.TryConsumeCore(RefreshIntentKind.OverwriteMetadata, itemId, itemPath);
        }

        private void SaveCore(RefreshIntentKind kind, Guid itemId, string? itemPath = null)
        {
            var normalizedPath = NormalizePath(itemPath);
            if (itemId == Guid.Empty && string.IsNullOrEmpty(normalizedPath))
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                this.Upsert(new RefreshIntentEntry(kind, itemId, normalizedPath, DateTimeOffset.UtcNow.Add(IntentLifetime)));
            }
        }

        private bool HasPendingCore(RefreshIntentKind kind, Guid itemId, string? itemPath = null)
        {
            var normalizedPath = NormalizePath(itemPath);

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                return this.TryResolveEntry(kind, itemId, normalizedPath, out _);
            }
        }

        private bool TryConsumeCore(RefreshIntentKind kind, Guid itemId, string? itemPath = null)
        {
            var normalizedPath = NormalizePath(itemPath);

            lock (this.syncRoot)
            {
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                if (!this.TryResolveEntry(kind, itemId, normalizedPath, out var entry))
                {
                    return false;
                }

                this.RemoveInternal(entry.Kind, entry.ItemId, entry.ItemPath);
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
            var entryKey = new RefreshIntentKey(entry.Kind, entry.ItemId);
            if (entry.ItemId != Guid.Empty && this.entriesByKey.TryGetValue(entryKey, out var existingById))
            {
                this.RemoveInternal(existingById.Kind, existingById.ItemId, existingById.ItemPath);
            }

            var pathKey = new RefreshIntentPathKey(entry.Kind, entry.ItemPath);
            if (!string.IsNullOrEmpty(entry.ItemPath)
                && this.itemKeysByPath.TryGetValue(pathKey, out var existingItemKey)
                && this.entriesByKey.TryGetValue(existingItemKey, out var existingByPath))
            {
                this.RemoveInternal(existingByPath.Kind, existingByPath.ItemId, existingByPath.ItemPath);
            }

            if (entry.ItemId != Guid.Empty)
            {
                this.entriesByKey[entryKey] = entry;
            }

            if (!string.IsNullOrEmpty(entry.ItemPath))
            {
                this.itemKeysByPath[pathKey] = entryKey;
            }
        }

        private bool TryResolveEntry(RefreshIntentKind kind, Guid itemId, string normalizedPath, [NotNullWhen(true)] out RefreshIntentEntry? entry)
        {
            if (itemId != Guid.Empty && this.entriesByKey.TryGetValue(new RefreshIntentKey(kind, itemId), out var entryById))
            {
                entry = entryById;
                return true;
            }

            if (!string.IsNullOrEmpty(normalizedPath)
                && this.itemKeysByPath.TryGetValue(new RefreshIntentPathKey(kind, normalizedPath), out var pathItemKey)
                && this.entriesByKey.TryGetValue(pathItemKey, out var entryByPath))
            {
                entry = entryByPath;
                return true;
            }

            entry = null;
            return false;
        }

        private void RemoveExpiredEntries(DateTimeOffset now)
        {
            if (this.entriesByKey.Count == 0)
            {
                return;
            }

            var expiredKeys = new List<RefreshIntentKey>();
            foreach (var pair in this.entriesByKey)
            {
                if (pair.Value.ExpiresAtUtc <= now)
                {
                    expiredKeys.Add(pair.Key);
                }
            }

            foreach (var expiredKey in expiredKeys)
            {
                if (this.entriesByKey.TryGetValue(expiredKey, out var entry))
                {
                    this.RemoveInternal(entry.Kind, entry.ItemId, entry.ItemPath);
                }
            }
        }

        private void RemoveInternal(RefreshIntentKind kind, Guid itemId, string itemPath)
        {
            if (itemId != Guid.Empty)
            {
                this.entriesByKey.Remove(new RefreshIntentKey(kind, itemId));
            }

            if (!string.IsNullOrEmpty(itemPath))
            {
                this.itemKeysByPath.Remove(new RefreshIntentPathKey(kind, itemPath));
            }
        }

        private readonly record struct RefreshIntentKey(RefreshIntentKind Kind, Guid ItemId);

        private readonly record struct RefreshIntentPathKey(RefreshIntentKind Kind, string ItemPath);

        private sealed class RefreshIntentEntry
        {
            public RefreshIntentEntry(RefreshIntentKind kind, Guid itemId, string itemPath, DateTimeOffset expiresAtUtc)
            {
                this.Kind = kind;
                this.ItemId = itemId;
                this.ItemPath = itemPath;
                this.ExpiresAtUtc = expiresAtUtc;
            }

            public RefreshIntentKind Kind { get; }

            public Guid ItemId { get; }

            public string ItemPath { get; }

            public DateTimeOffset ExpiresAtUtc { get; }
        }
    }
}
