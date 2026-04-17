// <copyright file="MissingMetadataSearchService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;
    using Microsoft.Extensions.Logging;

    internal enum MissingMetadataCandidateReason
    {
        CompleteMetadata = 0,
        EmptyId = 1,
        UnsupportedType = 2,
        MissingProviderIds = 3,
        MissingOverview = 4,
        MissingPrimaryImage = 5,
        DefaultEpisodeTitle = 6,
    }

    public sealed class MissingMetadataSearchService : IMissingMetadataSearchService
    {
        private static readonly TimeSpan QueueRefreshDelay = TimeSpan.FromSeconds(5);

        private static readonly BaseItemKind[] SupportedQueryItemTypes =
        {
            BaseItemKind.Movie,
            BaseItemKind.Series,
            BaseItemKind.Season,
            BaseItemKind.Episode,
            BaseItemKind.BoxSet,
        };

        private static readonly Action<ILogger, Exception?> LogRunPlaceholder =
            LoggerMessage.Define(LogLevel.Debug, new EventId(1, nameof(RunFullLibrarySearchAsync)), "Missing metadata full-library search service invoked.");

        private static readonly Action<ILogger, Exception?> LogRunSkippedBecauseAlreadyRunning =
            LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(RunFullLibrarySearchAsync)), "Missing metadata full-library search skipped because another run is already in progress.");

        private readonly ILogger<MissingMetadataSearchService> logger;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
        private int isRunning;

        public MissingMetadataSearchService(
            ILogger<MissingMetadataSearchService> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem)
            : this(logger, libraryManager, providerManager, fileSystem, (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
        {
        }

        internal MissingMetadataSearchService(
            ILogger<MissingMetadataSearchService> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(libraryManager);
            ArgumentNullException.ThrowIfNull(providerManager);
            ArgumentNullException.ThrowIfNull(fileSystem);
            ArgumentNullException.ThrowIfNull(delayAsync);

            this.logger = logger;
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.delayAsync = delayAsync;
        }

        public async Task RunFullLibrarySearchAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.CompareExchange(ref this.isRunning, 1, 0) != 0)
            {
                LogRunSkippedBecauseAlreadyRunning(this.logger, null);
                progress.Report(100);
                return;
            }

            try
            {
                LogRunPlaceholder(this.logger, null);

                var candidates = this.GetMissingMetadataCandidates();
                if (candidates.Count == 0)
                {
                    progress.Report(100);
                    return;
                }

                for (var processedCount = 0; processedCount < candidates.Count; processedCount++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var candidate = candidates[processedCount];
                    this.providerManager.QueueRefresh(candidate.Id, this.CreateRefreshOptions(), RefreshPriority.Normal);
                    progress.Report((processedCount + 1) * 100.0 / candidates.Count);

                    await this.delayAsync(QueueRefreshDelay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref this.isRunning, 0);
            }
        }

        internal static bool IsSupportedMissingMetadataItemType(BaseItem? item)
        {
            return item is Movie or Series or Season or Episode or BoxSet;
        }

        internal static bool IsMissingMetadataSearchCandidate(BaseItem? item)
        {
            return ResolveMissingMetadataCandidateReason(item) is MissingMetadataCandidateReason.MissingProviderIds
                or MissingMetadataCandidateReason.MissingOverview
                or MissingMetadataCandidateReason.MissingPrimaryImage
                or MissingMetadataCandidateReason.DefaultEpisodeTitle;
        }

        internal static MissingMetadataCandidateReason ResolveMissingMetadataCandidateReason(BaseItem? item)
        {
            if (item == null || item.Id == Guid.Empty)
            {
                return MissingMetadataCandidateReason.EmptyId;
            }

            if (!IsSupportedMissingMetadataItemType(item))
            {
                return MissingMetadataCandidateReason.UnsupportedType;
            }

            if (item.ProviderIds == null || item.ProviderIds.Count == 0)
            {
                return MissingMetadataCandidateReason.MissingProviderIds;
            }

            if (string.IsNullOrWhiteSpace(item.Overview))
            {
                return MissingMetadataCandidateReason.MissingOverview;
            }

            if (!item.HasImage(ImageType.Primary))
            {
                return MissingMetadataCandidateReason.MissingPrimaryImage;
            }

            if (item is Episode episode && EpisodeProvider.IsDefaultJellyfinEpisodeTitle(episode.Name))
            {
                return MissingMetadataCandidateReason.DefaultEpisodeTitle;
            }

            return MissingMetadataCandidateReason.CompleteMetadata;
        }

        private static InternalItemsQuery CreateFullLibraryQuery()
        {
            return new InternalItemsQuery
            {
                IncludeItemTypes = SupportedQueryItemTypes,
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
            };
        }

        private MetadataRefreshOptions CreateRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(this.fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
            };
        }

        private List<BaseItem> GetMissingMetadataCandidates()
        {
            var items = this.libraryManager.GetItemList(CreateFullLibraryQuery());
            return items.FindAll(IsMissingMetadataSearchCandidate);
        }
    }
}
