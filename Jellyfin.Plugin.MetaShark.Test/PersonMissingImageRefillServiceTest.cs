using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.BaseItemManager;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class PersonMissingImageRefillServiceTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());

        [TestMethod]
        public void QueueMissingImagesForFullLibraryScan_TracksOnlyPersonsMissingPrimaryImage()
        {
            InternalItemsQuery? capturedQuery = null;
            var missingPerson = new Person { Id = Guid.NewGuid(), Name = "Actor A" };
            missingPerson.SetProviderId(MetadataProvider.Tmdb, "1001");

            var personWithImage = new Person { Id = Guid.NewGuid(), Name = "Actor B" };
            personWithImage.SetImagePath(ImageType.Primary, "https://example.com/images/actor-b-primary.jpg");

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Callback<InternalItemsQuery>(query => capturedQuery = query)
                .Returns(new List<BaseItem> { missingPerson, personWithImage });

            var stateStore = new TestPersonImageRefillStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var service = this.CreateService(libraryManagerStub.Object, providerManagerStub.Object, stateStore);

            var summary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);
            var state = stateStore.GetState(missingPerson.Id);

            Assert.IsNotNull(capturedQuery);
            CollectionAssert.AreEqual(new[] { BaseItemKind.Person }, capturedQuery!.IncludeItemTypes!.ToArray());
            Assert.AreEqual(1, summary.CandidateCount);
            Assert.AreEqual(1, summary.QueuedCount);
            Assert.AreEqual(0, summary.SkippedCount);
            Assert.AreEqual("None", summary.SkippedReasons);
            providerManagerStub.Verify(
                x => x.QueueRefresh(
                    missingPerson.Id,
                    It.Is<MetadataRefreshOptions>(opt =>
                        opt.MetadataRefreshMode == MetadataRefreshMode.FullRefresh
                        && opt.ImageRefreshMode == MetadataRefreshMode.FullRefresh
                        && !opt.ReplaceAllMetadata
                        && !opt.ReplaceAllImages),
                    RefreshPriority.Normal),
                Times.Once);
            Assert.IsNotNull(state);
            Assert.AreEqual(PersonImageRefillStatus.Pending, state!.Status);
            Assert.AreEqual("QueuedRefresh", state.LastReason);
            Assert.AreEqual(1, state.AttemptCount);
            Assert.IsNull(stateStore.GetState(personWithImage.Id));
            Assert.AreEqual(1, stateStore.SaveCallCount);
        }

        [TestMethod]
        public void QueueMissingImagesForFullLibraryScan_DoesNotRequeuePendingPerson()
        {
            var missingPerson = new Person { Id = Guid.NewGuid(), Name = "Actor A" };
            missingPerson.SetProviderId(MetadataProvider.Tmdb, "1001");

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { missingPerson });

            var stateStore = new TestPersonImageRefillStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var service = this.CreateService(libraryManagerStub.Object, providerManagerStub.Object, stateStore);

            var firstSummary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);
            var secondSummary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);
            var state = stateStore.GetState(missingPerson.Id);

            Assert.AreEqual(1, firstSummary.QueuedCount);
            Assert.AreEqual(0, secondSummary.QueuedCount);
            Assert.AreEqual(1, secondSummary.SkippedCount);
            Assert.AreEqual("AlreadyPending=1", secondSummary.SkippedReasons);
            providerManagerStub.Verify(
                x => x.QueueRefresh(missingPerson.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Once);
            Assert.IsNotNull(state);
            Assert.AreEqual(PersonImageRefillStatus.Pending, state!.Status);
            Assert.AreEqual(1, stateStore.SaveCallCount);
        }

        [TestMethod]
        public void QueueMissingImagesForFullLibraryScan_QueuesRefreshEvenWhenPersonHasNoTmdbId()
        {
            var missingPerson = new Person
            {
                Id = Guid.NewGuid(),
                Name = "Actor A",
            };
            missingPerson.SetProviderId(BaseProvider.DoubanProviderId, "douban-1001");

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { missingPerson });

            var stateStore = new TestPersonImageRefillStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var service = this.CreateService(libraryManagerStub.Object, providerManagerStub.Object, stateStore);

            var summary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

            Assert.AreEqual(1, summary.QueuedCount);
            providerManagerStub.Verify(
                x => x.QueueRefresh(missingPerson.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Once,
                "缺少 TmDbId 的人物也应进入真实 refresh 执行路径，让 Douban-first 链路有机会补回。 ");
        }

        [TestMethod]
        public void QueueMissingImagesForFullLibraryScan_TreatsDeletedLocalPrimaryAsMissing()
        {
            var person = new Person { Id = Guid.NewGuid(), Name = "Actor A", DateCreated = DateTime.UtcNow };
            person.SetProviderId(MetadataProvider.Tmdb, "1001");

            var filePath = Path.Combine(Path.GetTempPath(), $"metashark-person-image-{Guid.NewGuid():N}.jpg");
            File.WriteAllText(filePath, "test");
            person.ImageInfos = new[]
            {
                new ItemImageInfo
                {
                    Type = ImageType.Primary,
                    Path = filePath,
                },
            };

            File.Delete(filePath);

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { person });

            var stateStore = new TestPersonImageRefillStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var service = this.CreateService(libraryManagerStub.Object, providerManagerStub.Object, stateStore);

            var summary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);
            var state = stateStore.GetState(person.Id);

            Assert.AreEqual(1, summary.CandidateCount);
            Assert.AreEqual(1, summary.QueuedCount);
            Assert.AreEqual(0, summary.SkippedCount);
            providerManagerStub.Verify(
                x => x.QueueRefresh(person.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Once,
                "本地主图文件已经被删掉时，应重新把人物视为缺图并排队刷新。 ");
            Assert.IsNotNull(state);
            Assert.AreEqual(PersonImageRefillStatus.Pending, state!.Status);
        }

        [TestMethod]
        public void RegisterServices_ShouldResolvePersonMissingImageRefillSingletonAndStateStore()
        {
            using var serviceProvider = CreateServiceProvider();

            var refillService = serviceProvider.GetRequiredService<IPersonMissingImageRefillService>();
            var refillServiceSecondResolve = serviceProvider.GetRequiredService<IPersonMissingImageRefillService>();
            var stateStore = serviceProvider.GetRequiredService<IPersonImageRefillStateStore>();
            var stateStoreSecondResolve = serviceProvider.GetRequiredService<IPersonImageRefillStateStore>();

            Assert.IsInstanceOfType(refillService, typeof(PersonMissingImageRefillService));
            Assert.AreSame(refillService, refillServiceSecondResolve);
            Assert.IsInstanceOfType(stateStore, typeof(FilePersonImageRefillStateStore));
            Assert.AreSame(stateStore, stateStoreSecondResolve);
        }

        private PersonMissingImageRefillService CreateService(ILibraryManager libraryManager, IProviderManager providerManager, IPersonImageRefillStateStore stateStore)
        {
            return new PersonMissingImageRefillService(this.loggerFactory, libraryManager, providerManager, new Mock<IFileSystem>().Object, stateStore);
        }

        private static ServiceProvider CreateServiceProvider()
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddLogging();
            serviceCollection.AddSingleton(Mock.Of<ILibraryManager>());
            serviceCollection.AddSingleton(Mock.Of<IProviderManager>());
            serviceCollection.AddSingleton(Mock.Of<IBaseItemManager>());
            serviceCollection.AddSingleton(Mock.Of<IFileSystem>());
            serviceCollection.AddSingleton(Mock.Of<ICollectionManager>());

            new ServiceRegistrator().RegisterServices(serviceCollection, Mock.Of<IServerApplicationHost>());
            serviceCollection.AddSingleton(Mock.Of<IEpisodeTitleBackfillPersistence>());
            serviceCollection.AddSingleton(Mock.Of<IEpisodeOverviewCleanupPersistence>());

            return serviceCollection.BuildServiceProvider();
        }
    }

    internal sealed class TestPersonImageRefillStateStore : IPersonImageRefillStateStore
    {
        private readonly Dictionary<Guid, PersonImageRefillState> states = new();

        public int SaveCallCount { get; private set; }

        public int RemoveCallCount { get; private set; }

        public PersonImageRefillState? GetState(Guid personId)
        {
            return this.states.TryGetValue(personId, out var state)
                ? Clone(state)
                : null;
        }

        public void Save(PersonImageRefillState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            this.SaveCallCount++;
            this.states[state.PersonId] = Clone(state);
        }

        public void Remove(Guid personId)
        {
            this.RemoveCallCount++;
            this.states.Remove(personId);
        }

        private static PersonImageRefillState Clone(PersonImageRefillState state)
        {
            return new PersonImageRefillState
            {
                PersonId = state.PersonId,
                Fingerprint = state.Fingerprint,
                Status = state.Status,
                AttemptCount = state.AttemptCount,
                LastReason = state.LastReason,
                NextRetryAtUtc = state.NextRetryAtUtc,
                UpdatedAtUtc = state.UpdatedAtUtc,
            };
        }
    }
}
