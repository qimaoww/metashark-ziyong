// <copyright file="EpisodeGroupRefreshCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using Jellyfin.Data.Enums;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;

    public sealed class EpisodeGroupRefreshCoordinator
    {
        private static readonly TimeSpan RecentlyQueuedWindow = TimeSpan.FromSeconds(30);

        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly EpisodeGroupRefreshService refreshService;
        private readonly ConcurrentDictionary<string, (string GroupId, DateTimeOffset ExpiresAt)> recentlyQueuedSeriesIds = new(StringComparer.OrdinalIgnoreCase);

        public EpisodeGroupRefreshCoordinator(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            EpisodeGroupRefreshService? refreshService = null)
        {
            this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            this.providerManager = providerManager ?? throw new ArgumentNullException(nameof(providerManager));
            this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            this.refreshService = refreshService ?? new EpisodeGroupRefreshService();
        }

        public EpisodeGroupRefreshQueueOutcome QueueAffectedSeriesRefresh(
            string? oldMapping,
            string? newMapping,
            Action<BaseItem>? emptyIdHandler = null,
            bool suppressRecentlyQueued = true,
            Func<string, string, bool>? additionalSkip = null,
            Action<string, string>? queuedHandler = null)
        {
            var refreshResult = this.refreshService.CreateRefreshResult(oldMapping, newMapping);
            if (refreshResult.AffectedSeriesIds.Count == 0)
            {
                return new EpisodeGroupRefreshQueueOutcome(refreshResult, 0);
            }

            var affectedSeriesIds = new HashSet<string>(refreshResult.AffectedSeriesIds, StringComparer.OrdinalIgnoreCase);
            var queueablePlans = EpisodeGroupRefreshQueueSelector.SelectQueueableRefreshPlans(
                this.libraryManager,
                this.fileSystem,
                affectedSeriesIds,
                item => item.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId) ? tmdbId : null);

            var queued = 0;
            var queuedGroupStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queuedHandlerStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in queueablePlans)
            {
                var newGroupId = refreshResult.NewSnapshot.TryGetGroupId(plan.GroupKey, out var resolvedGroupId)
                    ? resolvedGroupId
                    : string.Empty;

                if ((suppressRecentlyQueued && this.IsRecentlyQueued(plan.GroupKey, newGroupId))
                    || (additionalSkip?.Invoke(plan.GroupKey, newGroupId) ?? false))
                {
                    continue;
                }

                var queuedForPlan = EpisodeGroupRefreshQueueSelector.QueueRefreshTargets(
                    plan,
                    this.providerManager,
                    this.fileSystem,
                    emptyIdHandler);
                if (queuedForPlan <= 0)
                {
                    continue;
                }

                if (suppressRecentlyQueued)
                {
                    queuedGroupStates[plan.GroupKey] = newGroupId;
                }

                if (queuedHandler != null)
                {
                    queuedHandlerStates[plan.GroupKey] = newGroupId;
                }

                queued += queuedForPlan;
            }

            foreach (var state in queuedGroupStates)
            {
                this.MarkRecentlyQueued(state.Key, state.Value);
            }

            foreach (var state in queuedHandlerStates)
            {
                queuedHandler?.Invoke(state.Key, state.Value);
            }

            return new EpisodeGroupRefreshQueueOutcome(refreshResult, queued);
        }

        private bool IsRecentlyQueued(string seriesTmdbId, string groupId)
        {
            if (string.IsNullOrWhiteSpace(seriesTmdbId))
            {
                return false;
            }

            if (!this.recentlyQueuedSeriesIds.TryGetValue(seriesTmdbId, out var state))
            {
                return false;
            }

            if (state.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                this.recentlyQueuedSeriesIds.TryRemove(seriesTmdbId, out _);
                return false;
            }

            return string.Equals(state.GroupId, groupId ?? string.Empty, StringComparison.Ordinal);
        }

        private void MarkRecentlyQueued(string seriesTmdbId, string groupId)
        {
            if (string.IsNullOrWhiteSpace(seriesTmdbId))
            {
                return;
            }

            this.recentlyQueuedSeriesIds[seriesTmdbId] = (groupId ?? string.Empty, DateTimeOffset.UtcNow.Add(RecentlyQueuedWindow));
        }
    }

    public sealed record EpisodeGroupRefreshQueueOutcome(EpisodeGroupRefreshResult RefreshResult, int QueuedCount);
}
