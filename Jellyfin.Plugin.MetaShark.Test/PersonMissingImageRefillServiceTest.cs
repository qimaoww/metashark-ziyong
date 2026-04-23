using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.BaseItemManager;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
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
            var missingPerson = CreatePerson(tmdbPersonId: "1001", name: "Actor A");
            var personWithImage = CreatePerson(name: "Actor B", primaryImagePath: "https://example.com/images/actor-b-primary.jpg");
            var enabledMovie = CreateMovie(
                "Enabled Movies",
                "/library/movies/enabled/movie-a.mkv",
                CreateCurrentPerson("1001", nameof(PersonKind.Actor), "角色A", "Actor A"));

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Callback<InternalItemsQuery>(query => capturedQuery = query)
                .Returns(new List<BaseItem> { missingPerson, personWithImage });

            var stateStore = new TestPersonImageRefillStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var service = this.CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                stateStore,
                CreateSharedResolver(
                    new BaseItem[] { enabledMovie },
                    new Dictionary<BaseItem, LibraryOptions?>
                    {
                        [enabledMovie] = CreateImageLibraryOptions(nameof(Movie), imageAllowed: true),
                    }));

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
            var missingPerson = CreatePerson(tmdbPersonId: "1001");
            var enabledMovie = CreateMovie(
                "Enabled Movies",
                "/library/movies/enabled/movie-a.mkv",
                CreateCurrentPerson("1001", nameof(PersonKind.Actor), "角色A", "Actor A"));

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { missingPerson });

            var stateStore = new TestPersonImageRefillStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var service = this.CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                stateStore,
                CreateSharedResolver(
                    new BaseItem[] { enabledMovie },
                    new Dictionary<BaseItem, LibraryOptions?>
                    {
                        [enabledMovie] = CreateImageLibraryOptions(nameof(Movie), imageAllowed: true),
                    }));

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
        public void QueueMissingImagesForFullLibraryScan_DoesNotQueueWhenPersonHasNoResolvableOwnership()
        {
            var missingPerson = CreatePerson(doubanPersonId: "douban-1001");

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { missingPerson });

            var stateStore = new TestPersonImageRefillStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var service = this.CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                stateStore,
                CreateSharedResolver(Array.Empty<BaseItem>(), new Dictionary<BaseItem, LibraryOptions?>()));

            var summary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

            Assert.AreEqual(0, summary.QueuedCount);
            Assert.AreEqual(1, summary.SkippedCount);
            Assert.AreEqual("SharedEntityLibraryUnresolved=1", summary.SkippedReasons);
            providerManagerStub.Verify(
                x => x.QueueRefresh(missingPerson.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Never,
                "无法解析共享实体归属的人物不应排队 refresh。 ");
            Assert.IsNull(stateStore.GetState(missingPerson.Id));
            Assert.AreEqual(0, stateStore.SaveCallCount);
        }

        [TestMethod]
        public void QueueMissingImagesForFullLibraryScan_TreatsDeletedLocalPrimaryAsMissing()
        {
            var person = CreatePerson(tmdbPersonId: "1001", dateCreated: DateTime.UtcNow);
            var enabledSeries = CreateSeries(
                "Enabled Series",
                "/library/tv/enabled-series",
                CreateCurrentPerson("1001", nameof(PersonKind.Actor), "角色A", "Actor A"));

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
            var service = this.CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                stateStore,
                CreateSharedResolver(
                    new BaseItem[] { enabledSeries },
                    new Dictionary<BaseItem, LibraryOptions?>
                    {
                        [enabledSeries] = CreateImageLibraryOptions(nameof(Series), imageAllowed: true),
                    }));

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
        public void QueueMissingImagesForFullLibraryScan_DoesNotQueueWhenPersonLinkedOnlyToDisabledLibraries()
        {
            var person = CreatePerson(tmdbPersonId: "1001");
            var disabledMovie = CreateMovie(
                "Disabled Movies",
                "/library/movies/disabled/movie-a.mkv",
                CreateCurrentPerson("1001", nameof(PersonKind.Actor), "角色A", "Actor A"));
            var disabledSeries = CreateSeries(
                "Disabled Series",
                "/library/tv/disabled-series",
                CreateCurrentPerson("1001", nameof(PersonKind.Actor), "角色A", "Actor A"));

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { person });

            var stateStore = new TestPersonImageRefillStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var service = this.CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                stateStore,
                CreateSharedResolver(
                    new BaseItem[] { disabledMovie, disabledSeries },
                    new Dictionary<BaseItem, LibraryOptions?>
                    {
                        [disabledMovie] = CreateImageLibraryOptions(nameof(Movie), imageAllowed: false),
                        [disabledSeries] = CreateImageLibraryOptions(nameof(Series), imageAllowed: false),
                    }));

            var summary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

            Assert.AreEqual(1, summary.CandidateCount);
            Assert.AreEqual(0, summary.QueuedCount);
            Assert.AreEqual(1, summary.SkippedCount);
            Assert.AreEqual("CapabilityDisabledForResolvedLibrary=1", summary.SkippedReasons);
            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
            Assert.IsNull(stateStore.GetState(person.Id));
            Assert.AreEqual(0, stateStore.SaveCallCount);
        }

        [TestMethod]
        public void QueueMissingImagesForUpdatedItem_ImageUpdate_GateDeniedMarksRetryableInsteadOfCompleted()
        {
            var person = CreatePerson(tmdbPersonId: "1001");
            var disabledMovie = CreateMovie(
                "Disabled Movies",
                "/library/movies/disabled/movie-a.mkv",
                CreateCurrentPerson("1001", nameof(PersonKind.Actor), "角色A", "Actor A"));

            var stateStore = new TestPersonImageRefillStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var service = this.CreateService(
                new Mock<ILibraryManager>().Object,
                providerManagerStub.Object,
                stateStore,
                CreateSharedResolver(
                    new BaseItem[] { disabledMovie },
                    new Dictionary<BaseItem, LibraryOptions?>
                    {
                        [disabledMovie] = CreateImageLibraryOptions(nameof(Movie), imageAllowed: false),
                    }));

            service.QueueMissingImagesForUpdatedItem(
                new ItemChangeEventArgs
                {
                    Item = person,
                    UpdateReason = ItemUpdateType.ImageUpdate,
                },
                CancellationToken.None);

            var state = stateStore.GetState(person.Id);
            Assert.IsNotNull(state);
            Assert.AreEqual(PersonImageRefillStatus.Retryable, state!.Status);
            Assert.AreEqual("PrimaryImageStillMissingAfterImageUpdate", state.LastReason);
            Assert.AreEqual(1, state.AttemptCount);
            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
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

        private PersonMissingImageRefillService CreateService(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IPersonImageRefillStateStore stateStore,
            MetaSharkSharedEntityLibraryCapabilityResolver? sharedEntityLibraryCapabilityResolver = null)
        {
            return new PersonMissingImageRefillService(
                this.loggerFactory,
                libraryManager,
                providerManager,
                new Mock<IFileSystem>().Object,
                stateStore,
                sharedEntityLibraryCapabilityResolver);
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
            serviceCollection.AddSingleton(Mock.Of<Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill.IEpisodeTitleBackfillPersistence>());
            serviceCollection.AddSingleton(Mock.Of<IEpisodeOverviewCleanupPersistence>());

            return serviceCollection.BuildServiceProvider();
        }

        private static MetaSharkSharedEntityLibraryCapabilityResolver CreateSharedResolver(
            IEnumerable<BaseItem> relatedItems,
            IDictionary<BaseItem, LibraryOptions?> libraryOptionsByItem)
        {
            var queryResults = relatedItems.ToList();
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(queryResults);
            libraryManagerStub
                .Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns((BaseItem item) => libraryOptionsByItem.TryGetValue(item, out var libraryOptions) ? libraryOptions! : null!);

            return new MetaSharkSharedEntityLibraryCapabilityResolver(libraryManagerStub.Object);
        }

        private static LibraryOptions CreateImageLibraryOptions(string itemType, bool imageAllowed)
        {
            return new LibraryOptions
            {
                TypeOptions = new[]
                {
                    new TypeOptions
                    {
                        Type = itemType,
                        MetadataFetchers = Array.Empty<string>(),
                        ImageFetchers = imageAllowed ? new[] { MetaSharkPlugin.PluginName } : Array.Empty<string>(),
                    },
                },
            };
        }

        private static Person CreatePerson(
            string? tmdbPersonId = null,
            string? doubanPersonId = null,
            string name = "Actor A",
            string? primaryImagePath = null,
            DateTime? dateCreated = null)
        {
            var person = new Person
            {
                Id = Guid.NewGuid(),
                Name = name,
            };

            if (dateCreated.HasValue)
            {
                person.DateCreated = dateCreated.Value;
            }

            if (!string.IsNullOrWhiteSpace(tmdbPersonId))
            {
                person.SetProviderId(MetadataProvider.Tmdb, tmdbPersonId);
            }

            if (!string.IsNullOrWhiteSpace(doubanPersonId))
            {
                person.SetProviderId(BaseProvider.DoubanProviderId, doubanPersonId);
            }

            if (!string.IsNullOrWhiteSpace(primaryImagePath))
            {
                person.SetImagePath(ImageType.Primary, primaryImagePath);
            }

            return person;
        }

        private static RelatedMovie CreateMovie(string name, string path, params PersonInfo[] people)
        {
            var movie = new RelatedMovie
            {
                Id = Guid.NewGuid(),
                Name = name,
                Path = path,
            };

            movie.SetSimulatedPeople(people);
            return movie;
        }

        private static RelatedSeries CreateSeries(string name, string path, params PersonInfo[] people)
        {
            var series = new RelatedSeries
            {
                Id = Guid.NewGuid(),
                Name = name,
                Path = path,
            };

            series.SetSimulatedPeople(people);
            return series;
        }

        private static PersonInfo CreateCurrentPerson(string tmdbPersonId, string personTypeName, string role, string name)
        {
            var person = new PersonInfo
            {
                Name = name,
                Role = role,
            };

            var typeProperty = typeof(PersonInfo).GetProperty(nameof(PersonInfo.Type), BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(typeProperty);
            typeProperty!.SetValue(person, Enum.Parse(typeProperty.PropertyType, personTypeName, ignoreCase: false));
            person.SetProviderId(MetadataProvider.Tmdb, tmdbPersonId);
            return person;
        }
    }

    internal sealed class RelatedMovie : Movie
    {
        private readonly List<object> simulatedPeople = new List<object>();

        public void SetSimulatedPeople(IEnumerable<PersonInfo> people)
        {
            this.simulatedPeople.Clear();
            foreach (var person in people)
            {
                this.simulatedPeople.Add(person);
            }
        }

        private IEnumerable GetPeople()
        {
            return this.simulatedPeople;
        }
    }

    internal sealed class RelatedSeries : Series
    {
        private readonly List<object> simulatedPeople = new List<object>();

        public void SetSimulatedPeople(IEnumerable<PersonInfo> people)
        {
            this.simulatedPeople.Clear();
            foreach (var person in people)
            {
                this.simulatedPeople.Add(person);
            }
        }

        private IEnumerable GetPeople()
        {
            return this.simulatedPeople;
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
