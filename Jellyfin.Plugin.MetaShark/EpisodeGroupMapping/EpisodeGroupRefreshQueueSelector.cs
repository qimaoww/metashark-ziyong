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
    using MediaBrowser.Controller.Persistence;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
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
            Func<BaseItem, string?> groupKeySelector,
            ILinkedChildrenService? linkedChildrenService = null)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            ArgumentNullException.ThrowIfNull(fileSystem);
            ArgumentNullException.ThrowIfNull(affectedGroupKeys);
            ArgumentNullException.ThrowIfNull(groupKeySelector);

            // affectedGroupKeys 就是受影响剧集的 TMDb id（groupKeySelector 取 ProviderIds["Tmdb"]），
            // 下推到数据库后无需加载全库剧集，也只需对少量剧集做磁盘探测。
            IReadOnlyList<BaseItem> seriesItems = affectedGroupKeys.Count == 0
                ? Array.Empty<BaseItem>()
                : libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Series },
                    IsVirtualItem = false,
                    IsMissing = false,
                    Recursive = true,
                    HasTmdbId = true,
                    HasAnyProviderIds = new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [MetadataProvider.Tmdb.ToString()] = affectedGroupKeys.ToArray(),
                    },
                });

            var queueableSeriesItems = SelectQueueableItems(seriesItems, fileSystem, groupKeySelector);
            return queueableSeriesItems
                .Select(series => CreateRefreshPlan(libraryManager, series, affectedGroupKeys, groupKeySelector, linkedChildrenService))
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

        /// <summary>
        /// 剧集、季与分集共用同一套刷新选项：映射变更后必须让 provider 重新解析
        /// （FullRefresh），并允许覆盖既有字段以清除旧映射残留。
        /// </summary>
        public static MetadataRefreshOptions CreateRefreshOptions(IFileSystem fileSystem)
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

                var refreshOptions = CreateRefreshOptions(fileSystem);
                providerManager.QueueRefresh(target.Item.Id, refreshOptions, RefreshPriority.High);
                queued++;
            }

            return queued;
        }

        private static EpisodeGroupRefreshQueuePlan? CreateRefreshPlan(
            ILibraryManager libraryManager,
            BaseItem series,
            IReadOnlySet<string> affectedGroupKeys,
            Func<BaseItem, string?> groupKeySelector,
            ILinkedChildrenService? linkedChildrenService)
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
                IsVirtualItem = false,
                IsMissing = false,
            });
            var episodes = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                IsVirtualItem = false,
                IsMissing = false,

                // Jellyfin 12 默认把 OwnerId 非空的次版本排除在普通查询之外，
                // 多版本分集必须显式包含，否则映射变更后次版本仍保留旧元数据。
                IncludeOwnedItems = true,
            });

            var targetItems = new List<BaseItem>(seasons.Count + episodes.Count);
            var seenItemIds = new HashSet<Guid>();
            foreach (var item in seasons.Concat(episodes))
            {
                if (item.Id != Guid.Empty && seenItemIds.Add(item.Id))
                {
                    targetItems.Add(item);
                }
            }

            AddAlternateVersionTargets(libraryManager, linkedChildrenService, episodes, seenItemIds, targetItems);

            var itemTargets = targetItems
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

        private static void AddAlternateVersionTargets(
            ILibraryManager libraryManager,
            ILinkedChildrenService? linkedChildrenService,
            IReadOnlyList<BaseItem> episodes,
            HashSet<Guid> seenItemIds,
            List<BaseItem> targetItems)
        {
            if (linkedChildrenService == null || episodes.Count == 0)
            {
                return;
            }

            var episodeIds = episodes.Select(episode => episode.Id).Where(id => id != Guid.Empty).ToArray();
            if (episodeIds.Length == 0)
            {
                return;
            }

            // 跨库的 LinkedAlternateVersion 不在剧集的 AncestorIds 链上，只能按关系表补充；
            // LocalAlternateVersion 是同一条目的另一份文件，同样需要跟随映射刷新。
            foreach (var episodeId in linkedChildrenService.GetItemIdsWithAlternateVersions(episodeIds))
            {
                AddLinkedVersionTargets(libraryManager, linkedChildrenService, episodeId, (int)LinkedChildType.LinkedAlternateVersion, seenItemIds, targetItems);
                AddLinkedVersionTargets(libraryManager, linkedChildrenService, episodeId, (int)LinkedChildType.LocalAlternateVersion, seenItemIds, targetItems);
            }
        }

        private static void AddLinkedVersionTargets(
            ILibraryManager libraryManager,
            ILinkedChildrenService linkedChildrenService,
            Guid episodeId,
            int childType,
            HashSet<Guid> seenItemIds,
            List<BaseItem> targetItems)
        {
            foreach (var versionId in linkedChildrenService.GetLinkedChildrenIds(episodeId, childType))
            {
                if (versionId == Guid.Empty || !seenItemIds.Add(versionId))
                {
                    continue;
                }

                if (libraryManager.GetItemById(versionId) is BaseItem versionItem)
                {
                    targetItems.Add(versionItem);
                }
            }
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
