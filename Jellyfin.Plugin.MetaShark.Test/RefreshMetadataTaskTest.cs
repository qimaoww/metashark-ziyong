using Jellyfin.Plugin.MetaShark.ScheduledTasks;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
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
    public class RefreshMetadataTaskTest
    {
        [TestMethod]
        public async Task ExecuteAsync_WhenNoItemsNeedRefresh_LogsStartAndNoItems()
        {
            var loggerStub = CreateLoggerStub();
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem>());

            var providerManagerStub = new Mock<IProviderManager>();
            var progress = new ProgressRecorder();
            var task = CreateTask(loggerStub.Object, libraryManagerStub.Object, providerManagerStub.Object);

            await task.ExecuteAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { 100d }, progress.Values.ToArray());
            providerManagerStub.Verify(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()), Times.Never);
            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 开始刷新缺少 provider ID 的条目", messageContains: ["[MetaShark] 开始刷新缺少 provider ID 的条目"]);
            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 未找到缺少 provider ID 的条目", messageContains: ["[MetaShark] 未找到缺少 provider ID 的条目"]);
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenItemsNeedRefresh_QueuesRefreshAndLogsSummary()
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Movie A",
                Path = "/library/movies/movie-a.mkv",
            };

            var loggerStub = CreateLoggerStub();
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });

            var queueCalls = new List<QueueRefreshCall>();
            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) => queueCalls.Add(new QueueRefreshCall(itemId, options, priority)));

            var progress = new ProgressRecorder();
            var task = CreateTask(loggerStub.Object, libraryManagerStub.Object, providerManagerStub.Object);

            await task.ExecuteAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { 100d }, progress.Values.ToArray());
            Assert.AreEqual(1, queueCalls.Count);
            Assert.AreEqual(movie.Id, queueCalls[0].ItemId);
            Assert.AreEqual(RefreshPriority.Normal, queueCalls[0].Priority);
            Assert.AreEqual(MetadataRefreshMode.FullRefresh, queueCalls[0].Options.MetadataRefreshMode);
            Assert.AreEqual(MetadataRefreshMode.FullRefresh, queueCalls[0].Options.ImageRefreshMode);
            Assert.IsFalse(queueCalls[0].Options.ReplaceAllMetadata);
            Assert.IsFalse(queueCalls[0].Options.ReplaceAllImages);

            LogAssert.AssertLoggedOnce(loggerStub, LogLevel.Information, expectException: false, originalFormatContains: "[MetaShark] 开始刷新缺少 provider ID 的条目", messageContains: ["[MetaShark] 开始刷新缺少 provider ID 的条目"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Count"] = 1,
                },
                originalFormatContains: "[MetaShark] 找到 {Count} 个待刷新条目",
                messageContains: ["[MetaShark] 找到 1 个待刷新条目"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Name"] = "Movie A",
                    ["Id"] = movie.Id,
                },
                originalFormatContains: "[MetaShark] 已排队刷新条目",
                messageContains: ["[MetaShark] 已排队刷新条目", $"itemId={movie.Id}"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Count"] = 1,
                },
                originalFormatContains: "[MetaShark] 缺少 provider ID 的条目刷新排队完成",
                messageContains: ["[MetaShark] 缺少 provider ID 的条目刷新排队完成", "Count=1"]);
        }

        private static Mock<ILogger<RefreshMetadataTask>> CreateLoggerStub()
        {
            var loggerStub = new Mock<ILogger<RefreshMetadataTask>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            return loggerStub;
        }

        private static RefreshMetadataTask CreateTask(
            ILogger<RefreshMetadataTask> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager)
        {
            return new RefreshMetadataTask(logger, libraryManager, providerManager, Mock.Of<IFileSystem>());
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
