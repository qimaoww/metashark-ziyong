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
    public class TvMissingImageRefillItemUpdatedWorkerTest
    {
        [TestMethod]
        public async Task StartAsync_LogsItemUpdatedReasonAndForwardsEvent()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            var refillServiceStub = new Mock<ITvMissingImageRefillService>();
            var loggerStub = new Mock<ILogger<TvMissingImageRefillItemUpdatedWorker>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var worker = new TvMissingImageRefillItemUpdatedWorker(libraryManagerStub.Object, refillServiceStub.Object, loggerStub.Object);

            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var series = new Series { Id = Guid.NewGuid(), Name = "Series A" };
            libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = series,
                    UpdateReason = ItemUpdateType.MetadataImport,
                });

            refillServiceStub.Verify(
                x => x.QueueMissingImagesForUpdatedItem(
                    It.Is<ItemChangeEventArgs>(e => e.Item == series && e.UpdateReason == ItemUpdateType.MetadataImport),
                    CancellationToken.None),
                Times.Once);

            loggerStub.Verify(
                x => x.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("MetadataImport", StringComparison.Ordinal)),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
    }
}
