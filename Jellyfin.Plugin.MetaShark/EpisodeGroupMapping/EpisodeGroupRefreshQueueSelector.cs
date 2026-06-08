// <copyright file="EpisodeGroupRefreshQueueSelector.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Model.IO;

    internal static class EpisodeGroupRefreshQueueSelector
    {
        private enum QueueCandidatePathState
        {
            Unknown,
            Exists,
            Missing,
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
    }
}
