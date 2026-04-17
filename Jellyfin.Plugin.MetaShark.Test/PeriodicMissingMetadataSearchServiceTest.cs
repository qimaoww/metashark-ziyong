using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using CandidateReason = Jellyfin.Plugin.MetaShark.Workers.MissingMetadataCandidateReason;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class PeriodicMissingMetadataSearchServiceTest
    {
        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchMissingProviderIds()
        {
            var movie = CreateItem<Movie>("Movie A", includeProviderIds: false, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(movie, CandidateReason.MissingProviderIds, true);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchEmptyOverview()
        {
            var season = CreateItem<Season>("Season 1", includeProviderIds: true, includeOverview: false, includePrimaryImage: true);

            AssertCandidate(season, CandidateReason.MissingOverview, true);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchMissingPrimaryImage()
        {
            var boxSet = CreateItem<BoxSet>("Collection A", includeProviderIds: true, includeOverview: true, includePrimaryImage: false);

            AssertCandidate(boxSet, CandidateReason.MissingPrimaryImage, true);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldMatchDefaultEpisodeTitleOnlyForEpisode()
        {
            var episode = CreateItem<Episode>("第 1 集", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(episode, CandidateReason.DefaultEpisodeTitle, true);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldSkipCompleteSeriesEvenWhenNameLooksLikeDefaultEpisodeTitle()
        {
            var series = CreateItem<Series>("第 1 集", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(series, CandidateReason.CompleteMetadata, false);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldSkipUnsupportedType()
        {
            var person = CreateItem<Person>("Actor A", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(person, CandidateReason.UnsupportedType, false);
        }

        [TestMethod]
        public void ResolveMissingMetadataCandidateReason_ShouldSkipEmptyGuid()
        {
            var movie = CreateItem<Movie>("Movie B", id: Guid.Empty, includeProviderIds: true, includeOverview: true, includePrimaryImage: true);

            AssertCandidate(movie, CandidateReason.EmptyId, false);
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_ShouldQuerySupportedItemTypesWithExpectedFlags()
        {
            InternalItemsQuery? capturedQuery = null;

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Callback<InternalItemsQuery>(query => capturedQuery = query)
                .Returns(new List<BaseItem>());

            var service = CreateService(libraryManagerStub.Object);

            await service.RunFullLibrarySearchAsync(new ProgressRecorder(), CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(capturedQuery);
            Assert.IsNotNull(capturedQuery!.IncludeItemTypes);
            Assert.AreEqual(5, capturedQuery.IncludeItemTypes!.Length);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Series,
                    BaseItemKind.Season,
                    BaseItemKind.Episode,
                    BaseItemKind.BoxSet,
                },
                capturedQuery.IncludeItemTypes);
            Assert.IsTrue(capturedQuery.Recursive);
            Assert.IsFalse(capturedQuery.IsVirtualItem);
            Assert.IsFalse(capturedQuery.IsMissing);
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_WhenCandidatesAreEmpty_ShouldReport100AndSkipQueueing()
        {
            var completeMovie = CreateItem<Movie>("Movie Complete", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var completeSeason = CreateItem<Season>("Season Complete", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var unsupportedPerson = CreateItem<Person>("Person Non Candidate", includeProviderIds: false, includeOverview: false, includePrimaryImage: false);
            var progress = new ProgressRecorder();
            var delayInvocations = new List<DelayInvocation>();

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { completeMovie, completeSeason, unsupportedPerson });

            var providerManagerStub = new Mock<IProviderManager>();
            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                delayAsync: (delay, token) =>
                {
                    delayInvocations.Add(new DelayInvocation { Delay = delay, CancellationToken = token });
                    return Task.CompletedTask;
                });

            await service.RunFullLibrarySearchAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { 100d }, progress.Values.ToArray());
            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
            Assert.AreEqual(0, delayInvocations.Count);
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_WhenCandidatesExist_ShouldQueueOnlyCandidatesWithFixedRefreshOptionsAndDelay()
        {
            var missingProviderMovie = CreateItem<Movie>("Movie Missing Provider", includeProviderIds: false, includeOverview: true, includePrimaryImage: true);
            var missingOverviewSeries = CreateItem<Series>("Series Missing Overview", includeProviderIds: true, includeOverview: false, includePrimaryImage: true);
            var defaultTitleEpisode = CreateItem<Episode>("第 1 集", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var completeBoxSet = CreateItem<BoxSet>("Collection Complete", includeProviderIds: true, includeOverview: true, includePrimaryImage: true);
            var unsupportedPerson = CreateItem<Person>("Actor A", includeProviderIds: false, includeOverview: false, includePrimaryImage: false);
            var progress = new ProgressRecorder();
            var queueInvocations = new List<QueueRefreshInvocation>();
            var delayInvocations = new List<DelayInvocation>();
            using var cancellationTokenSource = new CancellationTokenSource();

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem>
                {
                    missingProviderMovie,
                    unsupportedPerson,
                    missingOverviewSeries,
                    defaultTitleEpisode,
                    completeBoxSet,
                });

            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) =>
                    queueInvocations.Add(new QueueRefreshInvocation
                    {
                        ItemId = itemId,
                        Options = options,
                        Priority = priority,
                    }));

            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                delayAsync: (delay, token) =>
                {
                    delayInvocations.Add(new DelayInvocation { Delay = delay, CancellationToken = token });
                    return Task.CompletedTask;
                });

            await service.RunFullLibrarySearchAsync(progress, cancellationTokenSource.Token).ConfigureAwait(false);

            Assert.AreEqual(3, progress.Values.Count);
            Assert.IsTrue(progress.Values.SequenceEqual(progress.Values.OrderBy(x => x)), "Progress should be monotonic increasing.");
            Assert.AreEqual(100d, progress.Values[progress.Values.Count - 1], 0.000001d);

            CollectionAssert.AreEqual(
                new[] { missingProviderMovie.Id, missingOverviewSeries.Id, defaultTitleEpisode.Id },
                queueInvocations.Select(x => x.ItemId).ToArray());

            Assert.AreEqual(3, delayInvocations.Count);
            Assert.IsTrue(delayInvocations.All(x => x.Delay == TimeSpan.FromSeconds(5)));
            Assert.IsTrue(delayInvocations.All(x => x.CancellationToken.Equals(cancellationTokenSource.Token)));

            foreach (var invocation in queueInvocations)
            {
                Assert.AreEqual(RefreshPriority.Normal, invocation.Priority);
                AssertFixedRefreshOptions(invocation.Options);
            }
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_WhenAnotherRunIsInFlight_ShouldSkipImmediatelyWithoutQueueingSecondRun()
        {
            var missingProviderMovie = CreateItem<Movie>("Movie Missing Provider", includeProviderIds: false, includeOverview: true, includePrimaryImage: true);
            var firstProgress = new ProgressRecorder();
            var secondProgress = new ProgressRecorder();
            var queueInvocations = new List<QueueRefreshInvocation>();
            var delayInvocations = new List<DelayInvocation>();
            var firstDelayEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstDelay = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { missingProviderMovie });

            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) =>
                    queueInvocations.Add(new QueueRefreshInvocation
                    {
                        ItemId = itemId,
                        Options = options,
                        Priority = priority,
                    }));

            var loggerStub = new Mock<ILogger<MissingMetadataSearchService>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                delayAsync: (delay, token) =>
                {
                    delayInvocations.Add(new DelayInvocation { Delay = delay, CancellationToken = token });
                    firstDelayEntered.TrySetResult(null);
                    return releaseFirstDelay.Task;
                },
                logger: loggerStub.Object);

            var firstRunTask = service.RunFullLibrarySearchAsync(firstProgress, CancellationToken.None);
            await firstDelayEntered.Task.ConfigureAwait(false);

            try
            {
                var secondRunTask = service.RunFullLibrarySearchAsync(secondProgress, CancellationToken.None);

                Assert.IsTrue(secondRunTask.IsCompleted, "Second invocation should skip immediately instead of waiting for the in-flight run.");
                Assert.IsFalse(firstRunTask.IsCompleted, "First invocation should still be blocked in delay when the second invocation returns.");

                await secondRunTask.ConfigureAwait(false);

                CollectionAssert.AreEqual(new[] { missingProviderMovie.Id }, queueInvocations.Select(x => x.ItemId).ToArray());
                Assert.AreEqual(1, delayInvocations.Count);
                CollectionAssert.AreEqual(new[] { 100d }, secondProgress.Values.ToArray());
                VerifyLoggedMessage(loggerStub, LogLevel.Information, "skipped because another run is already in progress");
            }
            finally
            {
                releaseFirstDelay.TrySetResult(null);
                await firstRunTask.ConfigureAwait(false);
            }
        }

        [TestMethod]
        public async Task RunFullLibrarySearchAsync_WhenDelayIsCanceled_ShouldPropagateOperationCanceledExceptionAndStopFurtherQueueing()
        {
            var missingProviderMovie = CreateItem<Movie>("Movie Missing Provider", includeProviderIds: false, includeOverview: true, includePrimaryImage: true);
            var missingOverviewSeries = CreateItem<Series>("Series Missing Overview", includeProviderIds: true, includeOverview: false, includePrimaryImage: true);
            var progress = new ProgressRecorder();
            var queueInvocations = new List<QueueRefreshInvocation>();
            var delayInvocations = new List<DelayInvocation>();
            var firstDelayEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationTokenSource = new CancellationTokenSource();

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { missingProviderMovie, missingOverviewSeries });

            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) =>
                    queueInvocations.Add(new QueueRefreshInvocation
                    {
                        ItemId = itemId,
                        Options = options,
                        Priority = priority,
                    }));

            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                delayAsync: (delay, token) =>
                {
                    delayInvocations.Add(new DelayInvocation { Delay = delay, CancellationToken = token });
                    firstDelayEntered.TrySetResult(null);

                    var delayTaskSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    token.Register(() => delayTaskSource.TrySetCanceled(token));
                    return delayTaskSource.Task;
                });

            var runTask = service.RunFullLibrarySearchAsync(progress, cancellationTokenSource.Token);
            await firstDelayEntered.Task.ConfigureAwait(false);

            cancellationTokenSource.Cancel();

            var exception = await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await runTask.ConfigureAwait(false)).ConfigureAwait(false);

            Assert.IsInstanceOfType(exception, typeof(OperationCanceledException));
            CollectionAssert.AreEqual(new[] { missingProviderMovie.Id }, queueInvocations.Select(x => x.ItemId).ToArray());
            Assert.AreEqual(1, delayInvocations.Count);
            Assert.AreEqual(1, progress.Values.Count);
            Assert.AreEqual(50d, progress.Values[0], 0.000001d);
        }

        private static MissingMetadataSearchService CreateService(
            ILibraryManager libraryManager,
            IProviderManager? providerManager = null,
            IFileSystem? fileSystem = null,
            Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
            ILogger<MissingMetadataSearchService>? logger = null)
        {
            return new MissingMetadataSearchService(
                logger ?? Mock.Of<ILogger<MissingMetadataSearchService>>(),
                libraryManager,
                providerManager ?? Mock.Of<IProviderManager>(),
                fileSystem ?? Mock.Of<IFileSystem>(),
                delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken)));
        }

        private static T CreateItem<T>(
            string name,
            Guid? id = null,
            bool includeProviderIds = true,
            bool includeOverview = true,
            bool includePrimaryImage = true)
            where T : BaseItem, new()
        {
            var item = new T
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
            };

            if (includeProviderIds)
            {
                item.SetProviderId(MetadataProvider.Tmdb, $"{typeof(T).Name}-tmdb-1");
            }

            if (includeOverview)
            {
                item.Overview = $"{typeof(T).Name} overview";
            }

            if (includePrimaryImage)
            {
                item.SetImagePath(ImageType.Primary, $"https://example.com/{typeof(T).Name.ToLowerInvariant()}-primary.jpg");
            }

            return item;
        }

        private static void AssertCandidate(BaseItem item, CandidateReason expectedReason, bool expectedCandidate)
        {
            var reason = MissingMetadataSearchService.ResolveMissingMetadataCandidateReason(item);
            var isCandidate = MissingMetadataSearchService.IsMissingMetadataSearchCandidate(item);

            Assert.AreEqual(expectedReason, reason);
            Assert.AreEqual(expectedCandidate, isCandidate);
        }

        private static void AssertFixedRefreshOptions(MetadataRefreshOptions options)
        {
            Assert.IsNotNull(options, "QueueRefresh should receive a MetadataRefreshOptions instance.");
            Assert.AreEqual(MetadataRefreshMode.FullRefresh, options.MetadataRefreshMode);
            Assert.AreEqual(MetadataRefreshMode.FullRefresh, options.ImageRefreshMode);
            Assert.IsFalse(options.ReplaceAllMetadata);
            Assert.IsFalse(options.ReplaceAllImages);
        }

        private static void VerifyLoggedMessage(Mock<ILogger<MissingMetadataSearchService>> loggerStub, LogLevel level, params string[] fragments)
        {
            loggerStub.Verify(
                x => x.Log(
                    level,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) => fragments.All(fragment => state.ToString()!.Contains(fragment, StringComparison.Ordinal))),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        private sealed class ProgressRecorder : IProgress<double>
        {
            public List<double> Values { get; } = new();

            public void Report(double value)
            {
                this.Values.Add(value);
            }
        }

        private sealed class QueueRefreshInvocation
        {
            public Guid ItemId { get; set; }

            public MetadataRefreshOptions Options { get; set; } = null!;

            public RefreshPriority Priority { get; set; }
        }

        private sealed class DelayInvocation
        {
            public TimeSpan Delay { get; set; }

            public CancellationToken CancellationToken { get; set; }
        }
    }
}
