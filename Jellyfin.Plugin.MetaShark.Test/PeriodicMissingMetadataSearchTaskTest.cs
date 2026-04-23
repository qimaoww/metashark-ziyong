using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.ScheduledTasks;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Tasks;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class PeriodicMissingMetadataSearchTaskTest
    {
        [TestMethod]
        public void MetadataProperties_ShouldMatchContract()
        {
            var task = new PeriodicMissingMetadataSearchTask(Mock.Of<IMissingMetadataSearchService>(), Mock.Of<IPersonMissingImageRefillService>());

            Assert.AreEqual("MetaSharkPeriodicMissingMetadataSearch", task.Key);
            Assert.AreEqual("定时搜索缺失元数据", task.Name);
            Assert.AreEqual("按计划扫描全库并搜索缺失元数据", task.Description);
            Assert.AreEqual(MetaSharkPlugin.PluginName, task.Category);
        }

        [TestMethod]
        public void GetDefaultTriggers_ShouldReturnSingleDailyMidnightTrigger()
        {
            var task = new PeriodicMissingMetadataSearchTask(Mock.Of<IMissingMetadataSearchService>(), Mock.Of<IPersonMissingImageRefillService>());
            var triggers = task.GetDefaultTriggers().ToArray();

            Assert.AreEqual(1, triggers.Length);
            Assert.AreEqual(TaskTriggerInfo.TriggerDaily, triggers[0].Type);
            Assert.AreEqual(TimeSpan.FromHours(0).Ticks, triggers[0].TimeOfDayTicks);
        }

        [TestMethod]
        public async Task ExecuteAsync_ShouldDelegateOnceAndPassThroughProgressAndToken()
        {
            var progress = new Progress<double>();
            using var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            IProgress<double>? forwardedProgress = null;
            CancellationToken forwardedToken = default;
            CancellationToken refillToken = default;

            var serviceStub = new Mock<IMissingMetadataSearchService>(MockBehavior.Strict);
            serviceStub
                .Setup(x => x.RunFullLibrarySearchAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                .Callback<IProgress<double>, CancellationToken>((receivedProgress, receivedToken) =>
                {
                    forwardedProgress = receivedProgress;
                    forwardedToken = receivedToken;
                })
                .Returns(Task.CompletedTask);

            var refillServiceStub = new Mock<IPersonMissingImageRefillService>(MockBehavior.Strict);
            refillServiceStub
                .Setup(x => x.QueueMissingImagesForFullLibraryScan(It.IsAny<CancellationToken>()))
                .Callback<CancellationToken>(token => refillToken = token)
                .Returns(new PersonMissingImageRefillScanSummary(0, 0, 0, "None"));

            var task = new PeriodicMissingMetadataSearchTask(serviceStub.Object, refillServiceStub.Object);

            await task.ExecuteAsync(progress, cancellationToken).ConfigureAwait(false);

            Assert.AreSame(progress, forwardedProgress);
            Assert.AreEqual(cancellationToken, forwardedToken);
            Assert.AreEqual(cancellationToken, refillToken);
            serviceStub.Verify(x => x.RunFullLibrarySearchAsync(progress, cancellationToken), Times.Once);
            refillServiceStub.Verify(x => x.QueueMissingImagesForFullLibraryScan(cancellationToken), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenTokenAlreadyCanceled_PropagatesCancellation()
        {
            var progress = new Progress<double>();
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();
            var cancellationToken = cancellationTokenSource.Token;
            IProgress<double>? forwardedProgress = null;
            CancellationToken forwardedToken = default;
            CancellationToken refillToken = default;
            var refillServiceStub = new Mock<IPersonMissingImageRefillService>(MockBehavior.Strict);
            refillServiceStub
                .Setup(x => x.QueueMissingImagesForFullLibraryScan(It.IsAny<CancellationToken>()))
                .Callback<CancellationToken>(token => refillToken = token)
                .Returns(new PersonMissingImageRefillScanSummary(0, 0, 0, "None"));

            var serviceStub = new Mock<IMissingMetadataSearchService>(MockBehavior.Strict);
            serviceStub
                .Setup(x => x.RunFullLibrarySearchAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                .Callback<IProgress<double>, CancellationToken>((receivedProgress, receivedToken) =>
                {
                    forwardedProgress = receivedProgress;
                    forwardedToken = receivedToken;
                })
                .Returns<IProgress<double>, CancellationToken>((_, receivedToken) =>
                {
                    receivedToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });

            var task = new PeriodicMissingMetadataSearchTask(serviceStub.Object, refillServiceStub.Object);

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
                await task.ExecuteAsync(progress, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreSame(progress, forwardedProgress);
            Assert.AreEqual(cancellationToken, forwardedToken);
            Assert.AreEqual(cancellationToken, refillToken);
            serviceStub.Verify(x => x.RunFullLibrarySearchAsync(progress, cancellationToken), Times.Once);
            refillServiceStub.Verify(x => x.QueueMissingImagesForFullLibraryScan(cancellationToken), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenDownstreamSearchContainsDisabledLibraries_StillInvokesServicesButQueuesOnlyEnabledRefreshes()
        {
            var enabledMovie = CreateMovieCandidate("Enabled Movie");
            var disabledMovie = CreateMovieCandidate("Disabled Movie");
            var progress = new ProgressRecorder();
            var queuedItemIds = new List<Guid>();
            CancellationToken refillToken = default;
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { enabledMovie, disabledMovie });
            libraryManagerStub
                .Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns<BaseItem>(item => CreateLibraryOptions(item.Id == enabledMovie.Id));

            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, _, _) => queuedItemIds.Add(itemId));

            var searchService = new MissingMetadataSearchService(
                Mock.Of<ILogger<MissingMetadataSearchService>>(),
                libraryManagerStub.Object,
                providerManagerStub.Object,
                Mock.Of<IFileSystem>(),
                new TestPeopleRefreshStateStore(),
                (_, _) => Task.CompletedTask);

            var refillServiceStub = new Mock<IPersonMissingImageRefillService>(MockBehavior.Strict);
            refillServiceStub
                .Setup(x => x.QueueMissingImagesForFullLibraryScan(It.IsAny<CancellationToken>()))
                .Callback<CancellationToken>(token => refillToken = token)
                .Returns(new PersonMissingImageRefillScanSummary(0, 0, 0, "None"));

            using var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            var task = new PeriodicMissingMetadataSearchTask(searchService, refillServiceStub.Object);

            await task.ExecuteAsync(progress, cancellationToken).ConfigureAwait(false);

            Assert.AreEqual(cancellationToken, refillToken);
            CollectionAssert.AreEqual(new[] { enabledMovie.Id }, queuedItemIds.ToArray());
            CollectionAssert.AreEqual(new[] { 100d }, progress.Values.ToArray());
            refillServiceStub.Verify(x => x.QueueMissingImagesForFullLibraryScan(cancellationToken), Times.Once);
        }

        private static Movie CreateMovieCandidate(string name)
        {
            return new Movie
            {
                Id = Guid.NewGuid(),
                Name = name,
                ProviderIds = new Dictionary<string, string>(),
                Overview = "Movie overview",
            };
        }

        private static LibraryOptions CreateLibraryOptions(bool metadataAllowed)
        {
            return new LibraryOptions
            {
                TypeOptions = new[]
                {
                    new TypeOptions
                    {
                        Type = nameof(Movie),
                        MetadataFetchers = metadataAllowed ? new[] { MetaSharkPlugin.PluginName } : Array.Empty<string>(),
                        ImageFetchers = Array.Empty<string>(),
                    },
                },
            };
        }

        private sealed class ProgressRecorder : IProgress<double>
        {
            public List<double> Values { get; } = new();

            public void Report(double value)
            {
                this.Values.Add(value);
            }
        }
    }
}
