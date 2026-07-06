// <copyright file="EpisodeGroupRefreshQueueSelector.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Jellyfin.Data.Enums;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.IO;

    internal static class EpisodeGroupRefreshQueueSelector
    {
        public enum EpisodeGroupRefreshQueueMode
        {
            SeriesFullRefresh,
            MetadataRefresh,
        }

        private enum QueueCandidatePathState
        {
            Unknown,
            Exists,
            Missing,
        }

        public static IReadOnlyList<EpisodeGroupRefreshQueuePlan> SelectQueueableRefreshPlans(
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            IReadOnlySet<string> affectedGroupKeys,
            Func<BaseItem, string?> groupKeySelector)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            ArgumentNullException.ThrowIfNull(fileSystem);
            ArgumentNullException.ThrowIfNull(affectedGroupKeys);
            ArgumentNullException.ThrowIfNull(groupKeySelector);

            var seriesItems = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
                HasTmdbId = true,
            });

            var queueableSeriesItems = SelectQueueableItems(seriesItems, fileSystem, groupKeySelector);
            return queueableSeriesItems
                .Select(series => CreateRefreshPlan(libraryManager, series, affectedGroupKeys, groupKeySelector))
                .Where(plan => plan != null)
                .Select(plan => plan!)
                .ToArray();
        }

        public static IReadOnlyList<BaseItem> SelectQueueableItems(
            IEnumerable<BaseItem> items,
            IFileSystem fileSystem,
            Func<BaseItem, string?> groupKeySelector)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(fileSystem);
            ArgumentNullException.ThrowIfNull(groupKeySelector);

            var candidates = items
                .Select(item => new QueueCandidate(item, groupKeySelector(item) ?? string.Empty, GetPathState(item, fileSystem)))
                .ToArray();
            var groupsWithExistingPath = candidates
                .Where(candidate => candidate.PathState == QueueCandidatePathState.Exists && !string.IsNullOrWhiteSpace(candidate.GroupKey))
                .Select(candidate => candidate.GroupKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return candidates
                .Where(candidate => !groupsWithExistingPath.Contains(candidate.GroupKey)
                    || candidate.PathState != QueueCandidatePathState.Missing)
                .Select(candidate => candidate.Item)
                .ToArray();
        }

        public static MetadataRefreshOptions CreateRefreshOptions(IFileSystem fileSystem, EpisodeGroupRefreshQueueMode mode)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);

            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                ReplaceAllImages = false,
            };

            refreshOptions.MetadataRefreshMode = MetadataRefreshMode.FullRefresh;
            refreshOptions.ImageRefreshMode = MetadataRefreshMode.FullRefresh;
            refreshOptions.ReplaceAllMetadata = true;
            refreshOptions.IsAutomated = false;
            return refreshOptions;
        }

        public static int QueueRefreshTargets(
            EpisodeGroupRefreshQueuePlan plan,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            Action<BaseItem>? emptyIdHandler = null)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(providerManager);
            ArgumentNullException.ThrowIfNull(fileSystem);

            var queued = 0;
            foreach (var target in plan.Targets)
            {
                if (target.Item.Id == Guid.Empty)
                {
                    emptyIdHandler?.Invoke(target.Item);
                    continue;
                }

                var refreshOptions = CreateRefreshOptions(fileSystem, target.Mode);
                providerManager.QueueRefresh(target.Item.Id, refreshOptions, RefreshPriority.High);
                queued++;
            }

            return queued;
        }

        private static EpisodeGroupRefreshQueuePlan? CreateRefreshPlan(
            ILibraryManager libraryManager,
            BaseItem series,
            IReadOnlySet<string> affectedGroupKeys,
            Func<BaseItem, string?> groupKeySelector)
        {
            var groupKey = groupKeySelector(series);
            if (string.IsNullOrWhiteSpace(groupKey) || !affectedGroupKeys.Contains(groupKey))
            {
                return null;
            }

            if (series.Id == Guid.Empty)
            {
                return new EpisodeGroupRefreshQueuePlan(
                    series,
                    groupKey,
                    new[]
                    {
                        new EpisodeGroupRefreshQueueTarget(series, EpisodeGroupRefreshQueueMode.SeriesFullRefresh),
                    });
            }

            var seasons = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Season },
                AncestorIds = new[] { series.Id },
            });
            var episodes = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                IsVirtualItem = false,
                IsMissing = false,
            });
            var itemTargets = seasons
                .Concat(episodes)
                .Select(item => new EpisodeGroupRefreshQueueTarget(item, EpisodeGroupRefreshQueueMode.MetadataRefresh))
                .ToArray();
            if (itemTargets.Length > 0)
            {
                return new EpisodeGroupRefreshQueuePlan(
                    series,
                    groupKey,
                    itemTargets);
            }

            return new EpisodeGroupRefreshQueuePlan(
                series,
                groupKey,
                new[]
                {
                    new EpisodeGroupRefreshQueueTarget(series, EpisodeGroupRefreshQueueMode.SeriesFullRefresh),
                });
        }

        private static QueueCandidatePathState GetPathState(BaseItem item, IFileSystem fileSystem)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (string.IsNullOrWhiteSpace(item.Path))
            {
                return QueueCandidatePathState.Unknown;
            }

            try
            {
                return fileSystem.DirectoryExists(item.Path) ? QueueCandidatePathState.Exists : QueueCandidatePathState.Missing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return QueueCandidatePathState.Unknown;
            }
        }

        private sealed record QueueCandidate(BaseItem Item, string GroupKey, QueueCandidatePathState PathState);

        public sealed record EpisodeGroupRefreshQueuePlan(BaseItem Series, string GroupKey, IReadOnlyList<EpisodeGroupRefreshQueueTarget> Targets);

        public sealed record EpisodeGroupRefreshQueueTarget(BaseItem Item, EpisodeGroupRefreshQueueMode Mode);
    }
}
