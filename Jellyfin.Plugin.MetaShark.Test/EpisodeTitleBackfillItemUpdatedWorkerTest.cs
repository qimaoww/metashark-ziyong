using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeTitleBackfillItemUpdatedWorkerTest
    {
        [TestMethod]
        public async Task StartAsync_ForwardsItemUpdatedEventToPostProcessService()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            var postProcessServiceStub = new Mock<IEpisodeTitleBackfillPostProcessService>();
            var loggerStub = new Mock<ILogger<EpisodeTitleBackfillItemUpdatedWorker>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            postProcessServiceStub
                .Setup(x => x.TryApplyAsync(It.IsAny<ItemChangeEventArgs>(), IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var worker = new EpisodeTitleBackfillItemUpdatedWorker(libraryManagerStub.Object, postProcessServiceStub.Object, loggerStub.Object);

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
                    It.Is<ItemChangeEventArgs>(e => e.Item == episode),
                    IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger,
                    CancellationToken.None),
                Times.Once);
        }

        [TestMethod]
        public async Task StartAsync_PassesMetadataImportReasonThroughToPostProcessService()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            var postProcessServiceStub = new Mock<IEpisodeTitleBackfillPostProcessService>();
            var loggerStub = new Mock<ILogger<EpisodeTitleBackfillItemUpdatedWorker>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            postProcessServiceStub
                .Setup(x => x.TryApplyAsync(It.IsAny<ItemChangeEventArgs>(), IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var worker = new EpisodeTitleBackfillItemUpdatedWorker(libraryManagerStub.Object, postProcessServiceStub.Object, loggerStub.Object);

            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode B", Path = "/library/tv/series-a/Season 01/episode-b.mkv" };
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
                    IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger,
                    CancellationToken.None),
                Times.Once);
        }

        [TestMethod]
        public async Task StartAsync_LogsAndRethrows_WhenPostProcessThrows()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            var postProcessServiceStub = new Mock<IEpisodeTitleBackfillPostProcessService>();
            var loggerStub = new Mock<ILogger<EpisodeTitleBackfillItemUpdatedWorker>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode C", Path = "/library/tv/series-a/Season 01/episode-c.mkv" };
            var expectedException = new InvalidOperationException("postprocess boom");
            postProcessServiceStub
                .Setup(x => x.TryApplyAsync(It.IsAny<ItemChangeEventArgs>(), IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger, CancellationToken.None))
                .ThrowsAsync(expectedException);

            var worker = new EpisodeTitleBackfillItemUpdatedWorker(libraryManagerStub.Object, postProcessServiceStub.Object, loggerStub.Object);

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
            loggerStub.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) =>
                        state.ToString()!.Contains(episode.Id.ToString(), StringComparison.Ordinal)
                        && state.ToString()!.Contains(episode.Path!, StringComparison.Ordinal)
                        && state.ToString()!.Contains("trigger=ItemUpdated", StringComparison.Ordinal)
                        && state.ToString()!.Contains("MetadataDownload", StringComparison.Ordinal)),
                    expectedException,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
    }
}
