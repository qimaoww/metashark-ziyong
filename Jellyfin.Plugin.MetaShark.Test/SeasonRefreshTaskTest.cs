using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class SeasonRefreshTaskTest
    {
        [TestMethod]
        public void MetadataProperties_ShouldMatchContract()
        {
            var task = CreateTask();

            Assert.AreEqual("MetaSharkSeasonRefresh", task.Key);
            Assert.AreEqual("季刷新", task.Name);
            Assert.AreEqual("对全库所有季执行扫描新的和有修改的文件", task.Description);
            Assert.AreEqual(MetaSharkPlugin.PluginName, task.Category);
        }

        [TestMethod]
        public void GetDefaultTriggers_ShouldReturnNoDefaultTriggers()
        {
            var task = CreateTask();

            Assert.AreEqual(0, task.GetDefaultTriggers().Count());
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenNoSeasonsExist_DoesNotQueueRefreshAndReportsComplete()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => IsAllSeasonsQuery(query))))
                .Returns(new List<BaseItem>());
            var providerManagerStub = new Mock<IProviderManager>(MockBehavior.Strict);
            var progress = new ProgressRecorder();
            var task = CreateTask(
                libraryManager: libraryManagerStub.Object,
                providerManager: providerManagerStub.Object);

            await task.ExecuteAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { 100d }, progress.Values.ToArray());
            libraryManagerStub.Verify(x => x.GetItemList(It.Is<InternalItemsQuery>(query => IsAllSeasonsQuery(query))), Times.Once);
            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenLibraryHasSeasons_QueuesAllSeasonScanRefreshes()
        {
            var firstSeason = CreateSeason(Guid.NewGuid(), "Season 1", Guid.NewGuid());
            var secondSeason = CreateSeason(Guid.NewGuid(), "Season 2", Guid.NewGuid());
            var virtualSeason = CreateSeason(Guid.NewGuid(), "Virtual Season", Guid.NewGuid());
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => IsAllSeasonsQuery(query))))
                .Returns(new List<BaseItem> { firstSeason, secondSeason, virtualSeason });

            var queueCalls = new List<QueueRefreshCall>();
            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) => queueCalls.Add(new QueueRefreshCall(itemId, options, priority)));

            var progress = new ProgressRecorder();
            var task = CreateTask(
                libraryManager: libraryManagerStub.Object,
                providerManager: providerManagerStub.Object);

            await task.ExecuteAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { 100d / 3d, 200d / 3d, 100d }, progress.Values.ToArray());
            CollectionAssert.AreEqual(
                new[] { firstSeason.Id, secondSeason.Id, virtualSeason.Id },
                queueCalls.Select(call => call.ItemId).ToArray());
            foreach (var queueCall in queueCalls)
            {
                Assert.AreEqual(RefreshPriority.High, queueCall.Priority);
                Assert.AreEqual(MetadataRefreshMode.Default, queueCall.Options.MetadataRefreshMode);
                Assert.AreEqual(MetadataRefreshMode.Default, queueCall.Options.ImageRefreshMode);
                Assert.IsFalse(queueCall.Options.ReplaceAllMetadata);
                Assert.IsFalse(queueCall.Options.ReplaceAllImages);
                Assert.IsFalse(queueCall.Options.IsAutomated);
            }

            libraryManagerStub.Verify(
                x => x.GetItemList(It.Is<InternalItemsQuery>(query => IsAllSeasonsQuery(query))),
                Times.Once);
        }

        private static SeasonRefreshTask CreateTask(
            ILibraryManager? libraryManager = null,
            IProviderManager? providerManager = null)
        {
            return new SeasonRefreshTask(
                Mock.Of<ILogger<SeasonRefreshTask>>(),
                libraryManager ?? Mock.Of<ILibraryManager>(),
                providerManager ?? Mock.Of<IProviderManager>(),
                Mock.Of<IFileSystem>());
        }

        private static Season CreateSeason(Guid id, string name, Guid seriesId)
        {
            return new Season
            {
                Id = id,
                Name = name,
                SeriesId = seriesId,
            };
        }

        private static bool IsAllSeasonsQuery(InternalItemsQuery query)
        {
            return HasSingleIncludeItemType(query, BaseItemKind.Season)
                && query.IsVirtualItem == null
                && query.IsMissing == null
                && query.ParentId == Guid.Empty
                && query.AncestorIds.Length == 0
                && query.Recursive;
        }

        private static bool HasSingleIncludeItemType(InternalItemsQuery query, BaseItemKind itemType)
        {
            return query.IncludeItemTypes.Length == 1
                && query.IncludeItemTypes[0] == itemType;
        }

        private sealed class ProgressRecorder : IProgress<double>
        {
            public List<double> Values { get; } = new();

            public void Report(double value)
            {
                this.Values.Add(value);
            }
        }

        private sealed record QueueRefreshCall(Guid ItemId, MetadataRefreshOptions Options, RefreshPriority Priority);
    }
}
