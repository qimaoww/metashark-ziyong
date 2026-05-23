using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
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
    public class PersonMissingImageRefillItemUpdatedWorkerTest
    {
        [TestMethod]
        public async Task StartAsync_LogsItemUpdatedReasonAndForwardsEvent()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            var refillServiceStub = new Mock<IPersonMissingImageRefillService>();
            var loggerStub = new Mock<ILogger<PersonMissingImageRefillItemUpdatedWorker>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var worker = new PersonMissingImageRefillItemUpdatedWorker(libraryManagerStub.Object, refillServiceStub.Object, loggerStub.Object);

            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var person = new Person { Id = Guid.NewGuid(), Name = "Actor A" };
            libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = person,
                    UpdateReason = ItemUpdateType.MetadataImport,
                });

            refillServiceStub.Verify(
                x => x.QueueMissingImagesForUpdatedItem(
                    It.Is<ItemChangeEventArgs>(e => e.Item == person && e.UpdateReason == ItemUpdateType.MetadataImport),
                    CancellationToken.None),
                Times.Once);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Name"] = person.Name,
                    ["Id"] = person.Id,
                    ["UpdateReason"] = ItemUpdateType.MetadataImport,
                },
                originalFormatContains: "[MetaShark] 收到人物缺图回填条目更新事件",
                messageContains: ["[MetaShark] 收到人物缺图回填条目更新事件", $"itemId={person.Id}", "updateReason=MetadataImport"]);
        }

        [TestMethod]
        public async Task StartAsync_Rethrows_WhenRefillServiceThrows()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            var refillServiceStub = new Mock<IPersonMissingImageRefillService>();
            var loggerStub = new Mock<ILogger<PersonMissingImageRefillItemUpdatedWorker>>();
            var expectedException = new InvalidOperationException("refill boom");
            refillServiceStub
                .Setup(x => x.QueueMissingImagesForUpdatedItem(It.IsAny<ItemChangeEventArgs>(), CancellationToken.None))
                .Throws(expectedException);

            var worker = new PersonMissingImageRefillItemUpdatedWorker(libraryManagerStub.Object, refillServiceStub.Object, loggerStub.Object);

            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var person = new Person { Id = Guid.NewGuid(), Name = "Actor B" };
            var actualException = Assert.ThrowsException<InvalidOperationException>(() => libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = person,
                    UpdateReason = ItemUpdateType.MetadataDownload,
                }));

            Assert.AreSame(expectedException, actualException);
            refillServiceStub.Verify(
                x => x.QueueMissingImagesForUpdatedItem(
                    It.Is<ItemChangeEventArgs>(e => e.Item == person && e.UpdateReason == ItemUpdateType.MetadataDownload),
                    CancellationToken.None),
                Times.Once);
        }
    }
}
