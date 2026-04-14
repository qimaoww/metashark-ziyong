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
                .Setup(x => x.ProcessUpdatedItemAsync(It.IsAny<ItemChangeEventArgs>(), CancellationToken.None))
                .Returns(Task.CompletedTask);

            var worker = new EpisodeTitleBackfillItemUpdatedWorker(libraryManagerStub.Object, postProcessServiceStub.Object, loggerStub.Object);

            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode A" };
            libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = episode,
                    UpdateReason = ItemUpdateType.MetadataImport,
                });

            postProcessServiceStub.Verify(
                x => x.ProcessUpdatedItemAsync(
                    It.Is<ItemChangeEventArgs>(e => e.Item == episode),
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
                .Setup(x => x.ProcessUpdatedItemAsync(It.IsAny<ItemChangeEventArgs>(), CancellationToken.None))
                .Returns(Task.CompletedTask);

            var worker = new EpisodeTitleBackfillItemUpdatedWorker(libraryManagerStub.Object, postProcessServiceStub.Object, loggerStub.Object);

            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode B" };
            libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = episode,
                    UpdateReason = ItemUpdateType.MetadataImport,
                });

            postProcessServiceStub.Verify(
                x => x.ProcessUpdatedItemAsync(
                    It.Is<ItemChangeEventArgs>(e => e.Item == episode && e.UpdateReason == ItemUpdateType.MetadataImport),
                    CancellationToken.None),
                Times.Once);
        }
    }
}
