using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class PersonMissingImageRefillStateMachineTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());

        [TestMethod]
        public void PersonMissingImageRefillService_RequiresRetryStateStoreContract()
        {
            Assert.IsTrue(
                HasConstructorParameterFragment(typeof(PersonMissingImageRefillService), "Store"),
                "PersonMissingImageRefillService needs a retry-state store abstraction before pending, retryable, and completed transitions can be implemented.");
        }

        [TestMethod]
        public void QueueMissingImagesForFullLibraryScan_DoesNotQueueRetryablePersonBeforeCooldownExpires()
        {
            var person = new Person { Id = Guid.NewGuid(), Name = "Actor A" };
            var stateStore = new TestPersonImageRefillStateStore();
            var service = this.CreateService(new List<BaseItem> { person }, stateStore, out var providerManagerStub);
            service.MarkRetryable(person, "NoRemoteImages", DateTimeOffset.UtcNow.AddMinutes(5));

            var summary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);
            var state = stateStore.GetState(person.Id);

            Assert.AreEqual(1, summary.CandidateCount);
            Assert.AreEqual(0, summary.QueuedCount);
            Assert.AreEqual(1, summary.SkippedCount);
            Assert.AreEqual("CooldownActive=1", summary.SkippedReasons);
            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
            Assert.IsNotNull(state);
            Assert.AreEqual(PersonImageRefillStatus.Retryable, state!.Status);
            Assert.AreEqual(1, state.AttemptCount);
        }

        [TestMethod]
        public void QueueMissingImagesForFullLibraryScan_RequeuesWhenFingerprintChanges()
        {
            var person = new Person { Id = Guid.NewGuid(), Name = "Actor A" };
            person.SetProviderId(MetadataProvider.Tmdb, "1001");

            var stateStore = new TestPersonImageRefillStateStore();
            stateStore.Save(new PersonImageRefillState
            {
                PersonId = person.Id,
                Fingerprint = "legacy-fingerprint",
                Status = PersonImageRefillStatus.Pending,
                AttemptCount = 7,
                LastReason = "QueuedScan",
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            });

            var service = this.CreateService(new List<BaseItem> { person }, stateStore, out var providerManagerStub);

            var summary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);
            var state = stateStore.GetState(person.Id);

            Assert.AreEqual(1, summary.CandidateCount);
            Assert.AreEqual(1, summary.QueuedCount);
            Assert.AreEqual(0, summary.SkippedCount);
            providerManagerStub.Verify(
                x => x.QueueRefresh(person.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Once);
            Assert.IsNotNull(state);
            Assert.AreEqual(PersonImageRefillStatus.Pending, state!.Status);
            Assert.AreEqual("QueuedRefresh", state.LastReason);
            Assert.AreEqual(1, state.AttemptCount);
            Assert.AreNotEqual("legacy-fingerprint", state.Fingerprint);
        }

        [TestMethod]
        public void QueueMissingImagesForUpdatedItem_MarksCompletedWhenImageUpdateAddsPrimaryImage()
        {
            var person = new Person { Id = Guid.NewGuid(), Name = "Actor A" };
            var stateStore = new TestPersonImageRefillStateStore();
            var service = this.CreateService(new List<BaseItem>(), stateStore, out var providerManagerStub);

            service.QueueMissingImagesForUpdatedItem(
                new ItemChangeEventArgs
                {
                    Item = person,
                    UpdateReason = ItemUpdateType.MetadataImport,
                },
                CancellationToken.None);
            person.SetImagePath(ImageType.Primary, "https://example.com/images/actor-a-primary.jpg");

            service.QueueMissingImagesForUpdatedItem(
                new ItemChangeEventArgs
                {
                    Item = person,
                    UpdateReason = ItemUpdateType.ImageUpdate,
                },
                CancellationToken.None);

            var state = stateStore.GetState(person.Id);
            providerManagerStub.Verify(
                x => x.QueueRefresh(person.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Once);
            Assert.IsNotNull(state);
            Assert.AreEqual(PersonImageRefillStatus.Completed, state!.Status);
            Assert.AreEqual("ImageUpdateCompleted", state.LastReason);
        }

        [TestMethod]
        public void QueueMissingImagesForUpdatedItem_MarksRetryableWhenImageUpdateStillLacksPrimaryImage()
        {
            var person = new Person { Id = Guid.NewGuid(), Name = "Actor A" };
            var stateStore = new TestPersonImageRefillStateStore();
            var service = this.CreateService(new List<BaseItem>(), stateStore, out var providerManagerStub);

            service.QueueMissingImagesForUpdatedItem(
                new ItemChangeEventArgs
                {
                    Item = person,
                    UpdateReason = ItemUpdateType.MetadataImport,
                },
                CancellationToken.None);

            service.QueueMissingImagesForUpdatedItem(
                new ItemChangeEventArgs
                {
                    Item = person,
                    UpdateReason = ItemUpdateType.ImageUpdate,
                },
                CancellationToken.None);

            var state = stateStore.GetState(person.Id);
            providerManagerStub.Verify(
                x => x.QueueRefresh(person.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Once);
            Assert.IsNotNull(state);
            Assert.AreEqual(PersonImageRefillStatus.Retryable, state!.Status);
            Assert.AreEqual("PrimaryImageStillMissingAfterImageUpdate", state.LastReason);
            Assert.IsTrue(state.NextRetryAtUtc.HasValue);
        }

        [TestMethod]
        public void QueueMissingImagesForUpdatedItem_NoTmdbId_RemainsRetryableAndNotCompletedAfterImageUpdate()
        {
            var person = new Person { Id = Guid.NewGuid(), Name = "Actor A" };
            person.SetProviderId(Jellyfin.Plugin.MetaShark.Providers.BaseProvider.DoubanProviderId, "douban-1001");

            var stateStore = new TestPersonImageRefillStateStore();
            var service = this.CreateService(new List<BaseItem>(), stateStore, out var providerManagerStub);

            service.QueueMissingImagesForUpdatedItem(
                new ItemChangeEventArgs
                {
                    Item = person,
                    UpdateReason = ItemUpdateType.MetadataImport,
                },
                CancellationToken.None);

            service.QueueMissingImagesForUpdatedItem(
                new ItemChangeEventArgs
                {
                    Item = person,
                    UpdateReason = ItemUpdateType.ImageUpdate,
                },
                CancellationToken.None);

            var state = stateStore.GetState(person.Id);
            providerManagerStub.Verify(
                x => x.QueueRefresh(person.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Once);
            Assert.IsNull(person.GetProviderId(MetadataProvider.Tmdb), "该用例必须明确覆盖无 TmDbId 的人物。 ");
            Assert.IsNotNull(state);
            Assert.AreNotEqual(PersonImageRefillStatus.Completed, state!.Status, "无 TmDbId 且更新后仍无图时，人物不应被误标为已完成。 ");
            Assert.AreEqual(PersonImageRefillStatus.Retryable, state.Status);
            Assert.AreEqual("PrimaryImageStillMissingAfterImageUpdate", state.LastReason);
            Assert.IsTrue(state.NextRetryAtUtc.HasValue);
        }

        [TestMethod]
        public void MarkCompleted_PersistsCompletedStateRecordWhenPrimaryImageExists()
        {
            var person = new Person { Id = Guid.NewGuid(), Name = "Actor A" };
            person.SetImagePath(ImageType.Primary, "https://example.com/images/actor-a-primary.jpg");

            var stateStore = new TestPersonImageRefillStateStore();
            var service = this.CreateService(new List<BaseItem>(), stateStore, out _);

            service.MarkCompleted(person, "PrimaryImagePresent");

            var state = stateStore.GetState(person.Id);
            Assert.IsNotNull(state);
            Assert.AreEqual(PersonImageRefillStatus.Completed, state!.Status);
            Assert.AreEqual("PrimaryImagePresent", state.LastReason);
        }

        [TestMethod]
        public void MarkCompleted_DoesNotSilentlyCompleteWhenPrimaryImageIsStillMissing()
        {
            var person = new Person { Id = Guid.NewGuid(), Name = "Actor A" };

            var stateStore = new TestPersonImageRefillStateStore();
            var service = this.CreateService(new List<BaseItem>(), stateStore, out _);

            service.MarkCompleted(person, "PrimaryImagePresent");

            var state = stateStore.GetState(person.Id);
            Assert.IsNotNull(state);
            Assert.AreEqual(PersonImageRefillStatus.Pending, state!.Status);
            Assert.AreEqual("PrimaryImagePresent", state.LastReason);
        }

        private PersonMissingImageRefillService CreateService(IReadOnlyList<BaseItem> items, IPersonImageRefillStateStore stateStore, out Mock<IProviderManager> providerManagerStub)
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(items.ToList());
            providerManagerStub = new Mock<IProviderManager>();

            return new PersonMissingImageRefillService(this.loggerFactory, libraryManagerStub.Object, providerManagerStub.Object, new Mock<IFileSystem>().Object, stateStore);
        }

        private static bool HasConstructorParameterFragment(Type type, string fragment)
        {
            return type.GetConstructors()
                .Any(ctor => ctor.GetParameters()
                    .Any(parameter =>
                        (parameter.ParameterType.Name ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase)
                        || (parameter.Name ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
