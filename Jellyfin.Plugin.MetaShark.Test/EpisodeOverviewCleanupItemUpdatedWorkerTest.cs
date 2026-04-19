using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeOverviewCleanupItemUpdatedWorkerTest
    {
        [TestMethod]
        public async Task StartAsync_ForwardsItemUpdatedEventToPostProcessServiceAndLogsStructuredMessage()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            var postProcessServiceStub = new Mock<IEpisodeOverviewCleanupPostProcessService>();
            var loggerStub = new Mock<ILogger<EpisodeOverviewCleanupItemUpdatedWorker>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            postProcessServiceStub
                .Setup(x => x.TryApplyAsync(It.IsAny<ItemChangeEventArgs>(), IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var worker = new EpisodeOverviewCleanupItemUpdatedWorker(libraryManagerStub.Object, postProcessServiceStub.Object, loggerStub.Object);

            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode A", Path = "/library/tv/series-a/Season 01/episode-a.mkv" };
            libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = episode,
                    UpdateReason = ItemUpdateType.MetadataImport,
                });

            postProcessServiceStub.Verify(
                x => x.TryApplyAsync(
                    It.Is<ItemChangeEventArgs>(e => e.Item == episode && e.UpdateReason == ItemUpdateType.MetadataImport),
                    IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger,
                    CancellationToken.None),
                Times.Once);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Name"] = episode.Name,
                    ["Id"] = episode.Id,
                    ["ItemPath"] = episode.Path,
                    ["UpdateReason"] = ItemUpdateType.MetadataImport,
                },
                originalFormatContains: "[MetaShark] 收到剧集简介清理条目更新事件",
                messageContains: ["[MetaShark] 收到剧集简介清理条目更新事件", "trigger=ItemUpdated", $"itemId={episode.Id}", $"itemPath={episode.Path}"]);
        }

        [TestMethod]
        public async Task StartAsync_LogsAndRethrows_WhenPostProcessThrows()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            var postProcessServiceStub = new Mock<IEpisodeOverviewCleanupPostProcessService>();
            var loggerStub = new Mock<ILogger<EpisodeOverviewCleanupItemUpdatedWorker>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode B", Path = "/library/tv/series-a/Season 01/episode-b.mkv" };
            var expectedException = new InvalidOperationException("postprocess boom");
            postProcessServiceStub
                .Setup(x => x.TryApplyAsync(It.IsAny<ItemChangeEventArgs>(), IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, CancellationToken.None))
                .ThrowsAsync(expectedException);

            var worker = new EpisodeOverviewCleanupItemUpdatedWorker(libraryManagerStub.Object, postProcessServiceStub.Object, loggerStub.Object);

            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var actualException = Assert.ThrowsException<InvalidOperationException>(() => libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = episode,
                    UpdateReason = ItemUpdateType.MetadataDownload,
                }));

            Assert.AreSame(expectedException, actualException);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Error,
                expectException: true,
                stateContains: new Dictionary<string, object?>
                {
                    ["Id"] = episode.Id,
                    ["ItemPath"] = episode.Path,
                    ["UpdateReason"] = ItemUpdateType.MetadataDownload,
                },
                originalFormatContains: "[MetaShark] 剧集简介清理后处理失败",
                messageContains: ["[MetaShark] 剧集简介清理后处理失败", "trigger=ItemUpdated", $"itemId={episode.Id}", $"itemPath={episode.Path}"]);
        }
    }
}
