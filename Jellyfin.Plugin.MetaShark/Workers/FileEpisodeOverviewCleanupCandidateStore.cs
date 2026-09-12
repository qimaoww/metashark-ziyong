// <copyright file="FileEpisodeOverviewCleanupCandidateStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Model;
    using Microsoft.Extensions.Logging;

    public sealed class FileEpisodeOverviewCleanupCandidateStore : IEpisodeOverviewCleanupCandidateStore
    {
        private static readonly Action<ILogger, string, Exception?> LogStateLoadFailed =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(EnsureLoaded)), "[MetaShark] 剧集简介清理候选加载失败，已重置状态. path={Path}.");

        private readonly object syncRoot = new object();
        private readonly ILogger<FileEpisodeOverviewCleanupCandidateStore> logger;
        private readonly string stateFilePath;
        private readonly Dictionary<string, Guid> itemIdsByPath = new Dictionary<string, Guid>(GetPathComparer());
        private DateTimeOffset nextSweepAtUtc;
        private Dictionary<Guid, EpisodeOverviewCleanupCandidate>? candidatesByItemId;

        public FileEpisodeOverviewCleanupCandidateStore(string stateFilePath, ILoggerFactory loggerFactory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
            ArgumentNullException.ThrowIfNull(loggerFactory);

            this.stateFilePath = stateFilePath;
            this.logger = loggerFactory.CreateLogger<FileEpisodeOverviewCleanupCandidateStore>();
        }

        public void Save(EpisodeOverviewCleanupCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);

                if (candidate.ItemId == Guid.Empty)
                {
                    return;
                }

                candidate.ClaimToken = string.Empty;
                this.Upsert(Clone(candidate));
                this.Write();
            }
        }

        public EpisodeOverviewCleanupCandidate? Peek(Guid itemId)
        {
            if (itemId == Guid.Empty)
            {
                return null;
            }

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                return this.candidatesByItemId!.TryGetValue(itemId, out var candidate) ? Clone(candidate) : null;
            }
        }

        public EpisodeOverviewCleanupCandidate? PeekByPath(string itemPath)
        {
            var normalizedPath = NormalizePath(itemPath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return null;
            }

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);
                if (!this.itemIdsByPath.TryGetValue(normalizedPath, out var itemId))
                {
                    return null;
                }

                return this.candidatesByItemId!.TryGetValue(itemId, out var candidate) ? Clone(candidate) : null;
            }
        }

        public EpisodeOverviewCleanupCandidate? TryClaim(Guid itemId, string itemPath, Guid currentItemId, string currentItemPath, string claimToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);

                var candidate = this.FindCandidate(itemId, itemPath);
                if (candidate == null || !string.IsNullOrEmpty(candidate.ClaimToken))
                {
                    return null;
                }

                var updatedItemId = currentItemId == Guid.Empty ? candidate.ItemId : currentItemId;
                var updatedItemPath = string.IsNullOrWhiteSpace(currentItemPath) ? candidate.ItemPath : currentItemPath;
                if (candidate.ItemId != updatedItemId || !PathMatches(candidate.ItemPath, updatedItemPath))
                {
                    this.RemoveInternal(candidate.ItemId, candidate.ItemPath);
                    candidate.ItemId = updatedItemId;
                    candidate.ItemPath = updatedItemPath;
                    this.candidatesByItemId![candidate.ItemId] = candidate;

                    var normalizedUpdatedPath = NormalizePath(candidate.ItemPath);
                    if (!string.IsNullOrEmpty(normalizedUpdatedPath))
                    {
                        this.itemIdsByPath[normalizedUpdatedPath] = candidate.ItemId;
                    }
                }

                candidate.ClaimToken = claimToken;
                this.Write();
                return Clone(candidate);
            }
        }

        public IReadOnlyList<EpisodeOverviewCleanupCandidate> GetDueDeferredRetries(DateTimeOffset nowUtc, int maxCount)
        {
            if (maxCount <= 0)
            {
                return Array.Empty<EpisodeOverviewCleanupCandidate>();
            }

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                this.RemoveExpiredEntries(nowUtc);

                var dueCandidates = new List<EpisodeOverviewCleanupCandidate>();
                foreach (var candidate in this.candidatesByItemId!.Values)
                {
                    if (candidate.NextAttemptAtUtc > nowUtc || !string.IsNullOrEmpty(candidate.ClaimToken))
                    {
                        continue;
                    }

                    dueCandidates.Add(Clone(candidate));
                }

                dueCandidates.Sort(static (left, right) =>
                {
                    var nextAttemptComparison = left.NextAttemptAtUtc.CompareTo(right.NextAttemptAtUtc);
                    if (nextAttemptComparison != 0)
                    {
                        return nextAttemptComparison;
                    }

                    return left.QueuedAtUtc.CompareTo(right.QueuedAtUtc);
                });

                if (dueCandidates.Count > maxCount)
                {
                    dueCandidates.RemoveRange(maxCount, dueCandidates.Count - maxCount);
                }

                return dueCandidates;
            }
        }

        public void UpdateDeferredRetry(EpisodeOverviewCleanupCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);

                if (candidate.ItemId == Guid.Empty)
                {
                    return;
                }

                if (this.candidatesByItemId!.TryGetValue(candidate.ItemId, out var existingCandidate)
                    && !string.IsNullOrEmpty(existingCandidate.ClaimToken))
                {
                    candidate.ClaimToken = existingCandidate.ClaimToken;
                }

                this.Upsert(Clone(candidate));
                this.Write();
            }
        }

        public void ReleaseClaim(Guid itemId, string itemPath, string claimToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);

                var candidate = this.FindCandidate(itemId, itemPath);
                if (candidate == null || !string.Equals(candidate.ClaimToken, claimToken, StringComparison.Ordinal))
                {
                    return;
                }

                candidate.ClaimToken = string.Empty;
                this.Write();
            }
        }

        public void Remove(Guid itemId, string itemPath)
        {
            var normalizedPath = NormalizePath(itemPath);

            lock (this.syncRoot)
            {
                this.EnsureLoaded();
                this.RemoveExpiredEntries(DateTimeOffset.UtcNow);

                var candidatesToRemove = new List<EpisodeOverviewCleanupCandidate>();
                if (itemId != Guid.Empty && this.candidatesByItemId!.TryGetValue(itemId, out var candidateById))
                {
                    candidatesToRemove.Add(candidateById);
                }

                if (!string.IsNullOrEmpty(normalizedPath)
                    && this.itemIdsByPath.TryGetValue(normalizedPath, out var candidateItemId)
                    && this.candidatesByItemId!.TryGetValue(candidateItemId, out var candidateByPath)
                    && !candidatesToRemove.Exists(candidate => candidate.ItemId == candidateByPath.ItemId))
                {
                    candidatesToRemove.Add(candidateByPath);
                }

                foreach (var candidate in candidatesToRemove)
                {
                    this.RemoveInternal(candidate.ItemId, candidate.ItemPath);
                }

                if (candidatesToRemove.Count > 0)
                {
                    this.Write();
                }
            }
        }

        private static EpisodeOverviewCleanupCandidate Clone(EpisodeOverviewCleanupCandidate candidate)
        {
            return new EpisodeOverviewCleanupCandidate
            {
                ItemId = candidate.ItemId,
                ItemPath = candidate.ItemPath,
                OriginalOverviewSnapshot = candidate.OriginalOverviewSnapshot,
                QueuedAtUtc = candidate.QueuedAtUtc,
                NextAttemptAtUtc = candidate.NextAttemptAtUtc,
                AttemptCount = candidate.AttemptCount,
                ClaimToken = candidate.ClaimToken,
                ExpiresAtUtc = candidate.ExpiresAtUtc,
            };
        }

        private static string NormalizePath(string itemPath)
        {
            return itemPath ?? string.Empty;
        }

        private static bool PathMatches(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static StringComparer GetPathComparer()
        {
            return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        }

        private void EnsureLoaded()
        {
            if (this.candidatesByItemId != null)
            {
                return;
            }

            this.candidatesByItemId = JsonStateFile.LoadOrReset<EpisodeOverviewCleanupCandidate>(
                this.stateFilePath,
                (path, exception) => LogStateLoadFailed(this.logger, path, exception));
            this.RebuildPathIndex();
        }

        private EpisodeOverviewCleanupCandidate? FindCandidate(Guid itemId, string itemPath)
        {
            if (itemId != Guid.Empty && this.candidatesByItemId!.TryGetValue(itemId, out var candidateById))
            {
                return candidateById;
            }

            var normalizedPath = NormalizePath(itemPath);
            if (!string.IsNullOrEmpty(normalizedPath)
                && this.itemIdsByPath.TryGetValue(normalizedPath, out var candidateItemId)
                && this.candidatesByItemId!.TryGetValue(candidateItemId, out var candidateByPath))
            {
                return candidateByPath;
            }

            return null;
        }

        private void RebuildPathIndex()
        {
            this.itemIdsByPath.Clear();
            foreach (var candidate in this.candidatesByItemId!.Values)
            {
                var normalizedPath = NormalizePath(candidate.ItemPath);
                if (!string.IsNullOrEmpty(normalizedPath))
                {
                    this.itemIdsByPath[normalizedPath] = candidate.ItemId;
                }
            }
        }

        private void RemoveExpiredEntries(DateTimeOffset nowUtc)
        {
            // 每次 Peek/TryClaim 都全表扫描并落盘会带来持续的 CPU 与磁盘抖动，这里摊还到每分钟一次。
            if (nowUtc < this.nextSweepAtUtc)
            {
                return;
            }

            this.nextSweepAtUtc = nowUtc.AddMinutes(1);

            var expiredCandidateIds = new List<Guid>();
            foreach (var candidate in this.candidatesByItemId!.Values)
            {
                if (candidate.ExpiresAtUtc <= nowUtc)
                {
                    expiredCandidateIds.Add(candidate.ItemId);
                }
            }

            foreach (var expiredCandidateId in expiredCandidateIds)
            {
                if (this.candidatesByItemId.TryGetValue(expiredCandidateId, out var expiredCandidate))
                {
                    this.RemoveInternal(expiredCandidate.ItemId, expiredCandidate.ItemPath);
                }
            }

            // 读路径不落盘：过期项已从内存移除，会在下一次真实写入时一并持久化。
        }

        private void Upsert(EpisodeOverviewCleanupCandidate candidate)
        {
            if (this.candidatesByItemId!.TryGetValue(candidate.ItemId, out var existingCandidate))
            {
                this.RemoveInternal(existingCandidate.ItemId, existingCandidate.ItemPath);
            }

            var normalizedPath = NormalizePath(candidate.ItemPath);
            if (!string.IsNullOrEmpty(normalizedPath)
                && this.itemIdsByPath.TryGetValue(normalizedPath, out var existingItemId)
                && this.candidatesByItemId.TryGetValue(existingItemId, out var existingPathCandidate))
            {
                this.RemoveInternal(existingPathCandidate.ItemId, existingPathCandidate.ItemPath);
            }

            this.candidatesByItemId[candidate.ItemId] = candidate;
            if (!string.IsNullOrEmpty(normalizedPath))
            {
                this.itemIdsByPath[normalizedPath] = candidate.ItemId;
            }
        }

        private void RemoveInternal(Guid itemId, string itemPath)
        {
            if (itemId != Guid.Empty)
            {
                _ = this.candidatesByItemId!.Remove(itemId);
            }

            var normalizedPath = NormalizePath(itemPath);
            if (!string.IsNullOrEmpty(normalizedPath)
                && this.itemIdsByPath.TryGetValue(normalizedPath, out var mappedItemId)
                && (itemId == Guid.Empty || mappedItemId == itemId))
            {
                _ = this.itemIdsByPath.Remove(normalizedPath);
            }
        }

        private void Write()
        {
            JsonStateFile.Write(this.stateFilePath, this.candidatesByItemId);
        }
    }
}
