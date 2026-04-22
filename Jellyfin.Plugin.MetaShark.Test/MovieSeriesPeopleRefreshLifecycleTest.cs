using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using CandidateReason = Jellyfin.Plugin.MetaShark.Workers.MissingMetadataCandidateReason;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class MovieSeriesPeopleRefreshLifecycleTest
    {
        [TestMethod]
        public async Task SearchMissingLifecycle_MovieWithoutState_MetadataDownloadShouldCloseStateAndStopRequeueing()
        {
            using var harness = FlowHarness<TrackingMovie>.CreateMovie();

            AssertCandidate(harness, harness.Item, CandidateReason.MissingPeopleRefreshState, true);

            await harness.RunSearchMissingAsync().ConfigureAwait(false);

            AssertQueuedOnceForPeopleRefresh(harness, harness.Item.Id);

            await harness.TriggerMetadataDownloadAsync().ConfigureAwait(false);

            AssertCurrentStatePersisted(harness);
            AssertCandidate(harness, harness.Item, CandidateReason.CompleteMetadata, false);

            await harness.RunSearchMissingAsync().ConfigureAwait(false);

            Assert.AreEqual(2, harness.SearchQueryInvocationCount, "第二次 search-missing 应只做候选判定，不应再次排队。 ");
            Assert.AreEqual(1, harness.QueueInvocations.Count, "state 已结清后，同一 movie 不应再次被 search-missing 入队。 ");
        }

        [TestMethod]
        public async Task SingleItemSearchMissingLifecycle_Movie_MetadataDownloadShouldQueueOverwriteWhenSameCountWrongSourceUntilAuthoritative()
        {
            using var harness = FlowHarness<TrackingMovie>.CreateMovie();
            var authoritativePeople = CreateAuthoritativePeopleSet();
            var authoritativeSnapshot = CreateAuthoritativePeopleSnapshot(harness.Item, authoritativePeople);
            harness.Item.SetSimulatedPeople(CreateNonAuthoritativePeopleWithSameCount());
            harness.OverwriteRefreshCandidateStore.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = harness.Item.Id,
                ItemPath = harness.Item.Path,
                ExpectedPeopleCount = authoritativePeople.Count,
                AuthoritativePeopleSnapshot = authoritativeSnapshot,
            });

            await harness.TriggerMetadataDownloadAsync().ConfigureAwait(false);

            Assert.IsNull(harness.StateStore.GetState(harness.Item.Id), "same-count 但来源错误时，首轮 MetadataDownload 不应误结清 people state。 ");
            Assert.AreEqual(0, harness.StateStore.SaveCallCount, "same-count 但非 authoritative 时，应先排队 overwrite，而不是提前写入 state store。 ");
            AssertQueuedOnceForSingleItemSearchMissingOverwrite(harness, harness.Item.Id);
            AssertPendingOverwriteCandidate(harness, expectedPeopleCount: authoritativePeople.Count, expectedAuthoritativePeopleSnapshot: authoritativeSnapshot);

            harness.Item.SetSimulatedPeople(authoritativePeople);

            await harness.TriggerMetadataDownloadAsync().ConfigureAwait(false);

            AssertCurrentStatePersisted(harness, expectedAuthoritativePeopleSnapshot: authoritativeSnapshot);
            Assert.AreEqual(1, harness.QueueInvocations.Count, "第二轮 authoritative 后不应再次排队 overwrite refresh。 ");
            Assert.IsNull(harness.OverwriteRefreshCandidateStore.Peek(harness.Item.Id), "movie 条目 authoritative 后，candidate 应真正消失。 ");
        }

        [TestMethod]
        public async Task SearchMissingLifecycle_SeriesWithStaleState_MetadataDownloadShouldCloseStateAndStopRequeueing()
        {
            using var harness = FlowHarness<TrackingSeries>.CreateSeries(stateVersion: "tmdb-people-strict-zh-cn-v0");

            AssertCandidate(harness, harness.Item, CandidateReason.MissingPeopleRefreshState, true);

            await harness.RunSearchMissingAsync().ConfigureAwait(false);

            AssertQueuedOnceForPeopleRefresh(harness, harness.Item.Id);

            await harness.TriggerMetadataDownloadAsync().ConfigureAwait(false);

            AssertCurrentStatePersisted(harness);
            AssertCandidate(harness, harness.Item, CandidateReason.CompleteMetadata, false);

            await harness.RunSearchMissingAsync().ConfigureAwait(false);

            Assert.AreEqual(2, harness.SearchQueryInvocationCount, "第二次 search-missing 应只做候选判定，不应再次排队。 ");
            Assert.AreEqual(1, harness.QueueInvocations.Count, "state 已从旧版本结清到当前版本后，同一 series 不应再次被 search-missing 入队。 ");
        }

        [TestMethod]
        public async Task SingleItemSearchMissingLifecycle_Series_MetadataDownloadShouldStayPendingUntilAuthoritativeEmptyClearsResidue()
        {
            using var harness = FlowHarness<TrackingSeries>.CreateSeries();
            var authoritativeSnapshot = CreateAuthoritativePeopleSnapshot(harness.Item, Array.Empty<PersonInfo>());
            harness.Item.SetSimulatedPeople(CreateLegacyResiduePeople());
            harness.OverwriteRefreshCandidateStore.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = harness.Item.Id,
                ItemPath = harness.Item.Path,
                ExpectedPeopleCount = 0,
                AuthoritativePeopleSnapshot = authoritativeSnapshot,
            });

            await harness.TriggerMetadataDownloadAsync().ConfigureAwait(false);

            Assert.IsNull(harness.StateStore.GetState(harness.Item.Id), "authoritative empty 但当前仍有旧人物时，series people state 应保持 pending。 ");
            Assert.AreEqual(0, harness.StateStore.SaveCallCount, "authoritative empty 遇到残留旧人物时，应继续排队 overwrite，而不是直接保存 state。 ");
            AssertQueuedOnceForSingleItemSearchMissingOverwrite(harness, harness.Item.Id);
            AssertPendingOverwriteCandidate(harness, expectedPeopleCount: 0, expectedAuthoritativePeopleSnapshot: authoritativeSnapshot);

            harness.Item.SetSimulatedPeople(Array.Empty<PersonInfo>());

            await harness.TriggerMetadataDownloadAsync().ConfigureAwait(false);

            AssertCurrentStatePersisted(harness, expectedAuthoritativePeopleSnapshot: authoritativeSnapshot);
            Assert.AreEqual(1, harness.QueueInvocations.Count, "series authoritative empty 成立后不应再次排队 overwrite refresh。 ");
            Assert.IsNull(harness.OverwriteRefreshCandidateStore.Peek(harness.Item.Id), "series 条目 authoritative empty 后，candidate 应真正消失。 ");
        }

        [TestMethod]
        public async Task SearchMissingLifecycle_SeriesWithLegacyProviderIdResidue_MetadataDownloadShouldCleanResidueAndStopRequeueing()
        {
            using var harness = FlowHarness<TrackingSeries>.CreateSeries(includeLegacyPeopleRefreshStateResidue: true);

            AssertCandidate(harness, harness.Item, CandidateReason.MissingPeopleRefreshState, true);
            Assert.IsTrue(harness.Item.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false, "测试前提：series 条目应先带有 legacy people state residue。 ");

            await harness.RunSearchMissingAsync().ConfigureAwait(false);

            AssertQueuedOnceForPeopleRefresh(harness, harness.Item.Id);

            await harness.TriggerMetadataDownloadAsync().ConfigureAwait(false);

            AssertCurrentStatePersisted(
                harness,
                expectedMetadataChangedCallCount: 1,
                expectedUpdateToRepositoryCallCount: 1,
                expectedLastUpdateReason: ItemUpdateType.MetadataEdit);
            AssertCandidate(harness, harness.Item, CandidateReason.CompleteMetadata, false);

            await harness.RunSearchMissingAsync().ConfigureAwait(false);

            Assert.AreEqual(2, harness.SearchQueryInvocationCount, "legacy residue 被清理并结清当前 state 后，第二次 search-missing 不应再次排队。 ");
            Assert.AreEqual(1, harness.QueueInvocations.Count, "legacy residue 清理后，同一 series 不应再次被 search-missing 入队。 ");
        }

        [TestMethod]
        public async Task OverwriteLifecycle_ReplaceAllMetadataMetadataDownloadShouldCloseStateWithoutSearchMissingPath()
        {
            using var harness = FlowHarness<TrackingMovie>.CreateMovie(stateVersion: "tmdb-people-strict-zh-cn-v0");
            var overwriteOptions = CreateOverwriteRefreshOptions();

            Assert.IsTrue(overwriteOptions.ReplaceAllMetadata, "overwrite 场景必须显式模拟 ReplaceAllMetadata=true。 ");
            AssertCandidate(harness, harness.Item, CandidateReason.MissingPeopleRefreshState, true);
            Assert.AreEqual(0, harness.SearchQueryInvocationCount, "overwrite 进入 MetadataDownload 前不应访问 search-missing 搜索链路。 ");
            Assert.AreEqual(0, harness.QueueInvocations.Count, "overwrite 进入 MetadataDownload 前不应依赖 search-missing candidate 排队。 ");

            await harness.TriggerMetadataDownloadAsync().ConfigureAwait(false);

            AssertCurrentStatePersisted(harness);
            AssertCandidate(harness, harness.Item, CandidateReason.CompleteMetadata, false);
            Assert.AreEqual(0, harness.SearchQueryInvocationCount, "overwrite 路径结清 state 时不应访问 search-missing candidate store/搜索链路。 ");
            Assert.AreEqual(0, harness.QueueInvocations.Count, "overwrite 路径应直接复用同一 post-process 结清逻辑，而不是依赖 search-missing candidate 排队。 ");
            Assert.IsNotNull(harness.Item.ProviderIds);
            Assert.AreEqual(1, harness.Item.ProviderIds!.Count, "overwrite 结清后不应把内部 people state 写回 provider ids。 ");
            CollectionAssert.AreEquivalent(new[] { MetadataProvider.Tmdb.ToString() }, harness.Item.ProviderIds.Keys.ToArray());
            Assert.IsFalse(harness.Item.ProviderIds.ContainsKey("MetaSharkPeopleRefreshState"), "overwrite 结清后不应保留 legacy people state provider id。 ");
        }

        private static void AssertCandidate<TItem>(FlowHarness<TItem> harness, BaseItem item, CandidateReason expectedReason, bool expectedCandidate)
            where TItem : BaseItem, ITrackingLifecycleItem
        {
            var state = harness.StateStore.GetState(item.Id);
            var reason = MissingMetadataSearchService.ResolveMissingMetadataCandidateReason(item, state);
            var isCandidate = MissingMetadataSearchService.IsMissingMetadataSearchCandidate(item, state);

            Assert.AreEqual(expectedReason, reason);
            Assert.AreEqual(expectedCandidate, isCandidate);
        }

        private static void AssertCurrentStatePersisted<TItem>(FlowHarness<TItem> harness, int expectedMetadataChangedCallCount = 0, int expectedUpdateToRepositoryCallCount = 0, ItemUpdateType? expectedLastUpdateReason = null, TmdbAuthoritativePeopleSnapshot? expectedAuthoritativePeopleSnapshot = null)
            where TItem : BaseItem, ITrackingLifecycleItem
        {
            var item = harness.Item;
            var state = harness.StateStore.GetState(item.Id);

            Assert.IsNotNull(state);
            Assert.AreEqual(item.Id, state!.ItemId);
            Assert.AreEqual(item is Movie ? nameof(Movie) : nameof(Series), state.ItemType);
            Assert.AreEqual(item.GetProviderId(MetadataProvider.Tmdb), state.TmdbId);
            Assert.AreEqual(PeopleRefreshState.CurrentVersion, state.Version);
            Assert.AreNotEqual(default, state.UpdatedAtUtc);
            Assert.AreEqual(expectedMetadataChangedCallCount, item.MetadataChangedCallCount, "metadata changed 调用次数与预期不一致。 ");
            Assert.AreEqual(expectedUpdateToRepositoryCallCount, item.UpdateToRepositoryCallCount, "repository 写回次数与预期不一致。 ");
            Assert.AreEqual(expectedLastUpdateReason, item.LastUpdateReason);
            Assert.IsNotNull(state.AuthoritativePeopleSnapshot, "结清后的 state 不应再是无 authoritative snapshot 的 current state。 ");
            if (expectedAuthoritativePeopleSnapshot != null)
            {
                Assert.IsTrue(state.AuthoritativePeopleSnapshot!.SetEquals(expectedAuthoritativePeopleSnapshot), "结清后的 authoritative snapshot 应与 candidate 一致。 ");
            }
            else
            {
                Assert.IsTrue(TmdbAuthoritativePeopleSnapshot.TryCreateFromCurrentItem(item, out var currentSnapshot));
                Assert.IsNotNull(currentSnapshot, "结清后的当前条目应可生成 authoritative snapshot。 ");
                Assert.IsTrue(state.AuthoritativePeopleSnapshot!.SetEquals(currentSnapshot), "normal settlement path 结清后的 snapshot 应与当前条目指纹一致。 ");
            }

            Assert.IsFalse(item.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false);
        }

        private static void AssertQueuedOnceForPeopleRefresh<TItem>(FlowHarness<TItem> harness, Guid expectedItemId)
            where TItem : BaseItem, ITrackingLifecycleItem
        {
            Assert.AreEqual(1, harness.SearchQueryInvocationCount, "search-missing 生命周期首轮应查询一次候选集合。 ");
            Assert.AreEqual(1, harness.QueueInvocations.Count, "首轮 search-missing 应只排队一次。 ");

            var invocation = harness.QueueInvocations.Single();
            Assert.AreEqual(expectedItemId, invocation.ItemId);
            Assert.AreEqual(RefreshPriority.Normal, invocation.Priority);
            Assert.AreEqual(MetadataRefreshMode.FullRefresh, invocation.Options.MetadataRefreshMode);
            Assert.AreEqual(MetadataRefreshMode.FullRefresh, invocation.Options.ImageRefreshMode);
            Assert.IsTrue(invocation.Options.ReplaceAllMetadata, "search-missing people backfill 应使用 overwrite 语义。 ");
            Assert.IsFalse(invocation.Options.ReplaceAllImages);
        }

        private static void AssertQueuedOnceForSingleItemSearchMissingOverwrite<TItem>(FlowHarness<TItem> harness, Guid expectedItemId)
            where TItem : BaseItem, ITrackingLifecycleItem
        {
            Assert.AreEqual(0, harness.SearchQueryInvocationCount, "内置单项 search-missing follow-up overwrite 不应触发全库搜索链路。 ");
            Assert.AreEqual(1, harness.QueueInvocations.Count, "内置单项 search-missing 首轮 MetadataDownload 应只排队一次 overwrite refresh。 ");

            var invocation = harness.QueueInvocations.Single();
            Assert.AreEqual(expectedItemId, invocation.ItemId);
            Assert.AreEqual(RefreshPriority.Normal, invocation.Priority);
            Assert.AreEqual(MetadataRefreshMode.FullRefresh, invocation.Options.MetadataRefreshMode);
            Assert.AreEqual(MetadataRefreshMode.FullRefresh, invocation.Options.ImageRefreshMode);
            Assert.IsTrue(invocation.Options.ReplaceAllMetadata, "follow-up overwrite refresh 必须显式使用 ReplaceAllMetadata=true。 ");
            Assert.IsFalse(invocation.Options.ReplaceAllImages);
        }

        private static void AssertPendingOverwriteCandidate<TItem>(FlowHarness<TItem> harness, int expectedPeopleCount, TmdbAuthoritativePeopleSnapshot? expectedAuthoritativePeopleSnapshot = null)
            where TItem : BaseItem, ITrackingLifecycleItem
        {
            var candidate = harness.OverwriteRefreshCandidateStore.Peek(harness.Item.Id);

            Assert.IsNotNull(candidate, "人数仍不足时应保留 pending overwrite candidate。 ");
            Assert.AreEqual(harness.Item.Id, candidate!.ItemId);
            Assert.AreEqual(harness.Item.Path, candidate.ItemPath);
            Assert.AreEqual(expectedPeopleCount, candidate.ExpectedPeopleCount);
            if (expectedAuthoritativePeopleSnapshot != null)
            {
                Assert.IsNotNull(candidate.AuthoritativePeopleSnapshot, "pending candidate 应保留 authoritative people snapshot。 ");
                Assert.IsTrue(candidate.AuthoritativePeopleSnapshot!.SetEquals(expectedAuthoritativePeopleSnapshot), "pending candidate 的 authoritative snapshot 应与 provider 结果一致。 ");
            }

            Assert.IsTrue(candidate.OverwriteQueued, "pending candidate 应标记为 overwrite 已排队，避免重复排队。 ");
        }

        private static MetadataRefreshOptions CreateOverwriteRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(Mock.Of<IFileSystem>()))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ReplaceAllImages = false,
            };
        }

        private static IReadOnlyList<PersonInfo> CreateAuthoritativePeopleSet()
        {
            return new[]
            {
                CreatePerson("1001", "Actor", "角色A", "TMDb Actor A"),
                CreatePerson("2001", "Director", "Director", "TMDb Director A"),
            };
        }

        private static IReadOnlyList<PersonInfo> CreateNonAuthoritativePeopleWithSameCount()
        {
            return new[]
            {
                CreatePerson(tmdbPersonId: null, "Actor", "角色A", "旧演员A"),
                CreatePerson(tmdbPersonId: null, "Director", "Director", "旧导演A"),
            };
        }

        private static IReadOnlyList<PersonInfo> CreateLegacyResiduePeople()
        {
            return new[]
            {
                CreatePerson(tmdbPersonId: null, "Actor", "角色A", "旧演员A"),
            };
        }

        private static TmdbAuthoritativePeopleSnapshot CreateAuthoritativePeopleSnapshot(BaseItem item, IEnumerable<PersonInfo> people)
        {
            return TmdbAuthoritativePeopleSnapshot.Create(
                item is Movie ? nameof(Movie) : nameof(Series),
                item.GetProviderId(MetadataProvider.Tmdb) ?? throw new InvalidOperationException("测试前提：item 必须带有 TMDb provider id。 "),
                people);
        }

        private static PersonInfo CreatePerson(string? tmdbPersonId, string personTypeName, string role, string name)
        {
            var person = new PersonInfo
            {
                Name = name,
                Role = role,
            };

            var typeProperty = typeof(PersonInfo).GetProperty("Type", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(typeProperty, "PersonInfo.Type 未定义。 ");
            Assert.IsTrue(typeProperty!.PropertyType.IsEnum, "PersonInfo.Type 应为枚举类型。 ");
            typeProperty.SetValue(person, Enum.Parse(typeProperty.PropertyType, personTypeName, ignoreCase: false));

            if (!string.IsNullOrWhiteSpace(tmdbPersonId))
            {
                person.SetProviderId(MetadataProvider.Tmdb, tmdbPersonId);
            }

            return person;
        }

        private interface ITrackingLifecycleItem
        {
            int MetadataChangedCallCount { get; }

            int UpdateToRepositoryCallCount { get; }

            ItemUpdateType? LastUpdateReason { get; }

            void SetSimulatedPeople(IEnumerable<PersonInfo> people);

            void SetSimulatedPeopleCount(int count);
        }

        private sealed class FlowHarness<TItem> : IDisposable
            where TItem : BaseItem, ITrackingLifecycleItem
        {
            private readonly Mock<ILibraryManager> libraryManagerStub;
            private readonly MissingMetadataSearchService missingMetadataSearchService;
            private readonly MovieSeriesPeopleRefreshStateItemUpdatedWorker worker;
            private bool workerStarted;

            private FlowHarness(TItem item)
            {
                this.Item = item;
                this.StateStore = new TestPeopleRefreshStateStore();
                this.OverwriteRefreshCandidateStore = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                this.QueueInvocations = new List<QueueRefreshInvocation>();
                this.libraryManagerStub = new Mock<ILibraryManager>();
                this.libraryManagerStub
                    .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                    .Callback(() => this.SearchQueryInvocationCount++)
                    .Returns(new List<BaseItem> { this.Item });

                var providerManagerStub = new Mock<IProviderManager>();
                providerManagerStub
                    .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                    .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) =>
                        this.QueueInvocations.Add(new QueueRefreshInvocation
                        {
                            ItemId = itemId,
                            Options = options,
                            Priority = priority,
                        }));

                this.missingMetadataSearchService = new MissingMetadataSearchService(
                    Mock.Of<ILogger<MissingMetadataSearchService>>(),
                    this.libraryManagerStub.Object,
                    providerManagerStub.Object,
                    Mock.Of<IFileSystem>(),
                    this.StateStore,
                    (delay, cancellationToken) => Task.CompletedTask);

                var postProcessService = new MovieSeriesPeopleRefreshStatePostProcessService(
                    Mock.Of<ILogger<MovieSeriesPeopleRefreshStatePostProcessService>>(),
                    this.StateStore,
                    providerManagerStub.Object,
                    this.OverwriteRefreshCandidateStore,
                    Mock.Of<IFileSystem>());
                this.worker = new MovieSeriesPeopleRefreshStateItemUpdatedWorker(
                    this.libraryManagerStub.Object,
                    postProcessService,
                    Mock.Of<ILogger<MovieSeriesPeopleRefreshStateItemUpdatedWorker>>());
            }

            public TItem Item { get; }

            public TestPeopleRefreshStateStore StateStore { get; }

            public InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore OverwriteRefreshCandidateStore { get; }

            public int SearchQueryInvocationCount { get; private set; }

            public List<QueueRefreshInvocation> QueueInvocations { get; }

            public static FlowHarness<TrackingMovie> CreateMovie(string? stateVersion = null)
            {
                var movie = new TrackingMovie
                {
                    Id = Guid.NewGuid(),
                    Name = "Movie Lifecycle",
                    Path = "/library/movies/movie-lifecycle/movie-lifecycle.mkv",
                    Overview = "movie overview",
                };

                movie.SetProviderId(MetadataProvider.Tmdb, "123456");
                movie.SetImagePath(ImageType.Primary, "https://example.com/movie.jpg");
                var harness = new FlowHarness<TrackingMovie>(movie);
                if (!string.IsNullOrWhiteSpace(stateVersion))
                {
                    PeopleRefreshStateTestHelper.SaveState(harness.StateStore, harness.Item, stateVersion);
                }

                return harness;
            }

            public static FlowHarness<TrackingSeries> CreateSeries(string? stateVersion = null, bool includeLegacyPeopleRefreshStateResidue = false)
            {
                var series = new TrackingSeries
                {
                    Id = Guid.NewGuid(),
                    Name = "Series Lifecycle",
                    Path = "/library/tv/series-lifecycle",
                    Overview = "series overview",
                };

                series.SetProviderId(MetadataProvider.Tmdb, "654321");
                series.SetImagePath(ImageType.Primary, "https://example.com/series.jpg");
                var harness = new FlowHarness<TrackingSeries>(series);
                if (!string.IsNullOrWhiteSpace(stateVersion))
                {
                    PeopleRefreshStateTestHelper.SaveState(harness.StateStore, harness.Item, stateVersion);
                }

                if (includeLegacyPeopleRefreshStateResidue)
                {
                    harness.Item.ProviderIds!["MetaSharkPeopleRefreshState"] = "tmdb-people-strict-zh-cn-v0";
                }

                return harness;
            }

            public Task RunSearchMissingAsync()
            {
                return this.missingMetadataSearchService.RunFullLibrarySearchAsync(new ProgressRecorder(), CancellationToken.None);
            }

            public async Task TriggerMetadataDownloadAsync()
            {
                if (!this.workerStarted)
                {
                    await this.worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    this.workerStarted = true;
                }

                this.libraryManagerStub.Raise(
                    x => x.ItemUpdated += null,
                    this.libraryManagerStub.Object,
                    new ItemChangeEventArgs
                    {
                        Item = this.Item,
                        UpdateReason = ItemUpdateType.MetadataDownload,
                    });
            }

            public void Dispose()
            {
                if (this.workerStarted)
                {
                    this.worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
            }
        }

        private sealed class TrackingMovie : Movie, ITrackingLifecycleItem
        {
            private readonly List<object> simulatedPeople = new List<object>();

            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public ItemUpdateType? LastUpdateReason { get; private set; }

            public void SetSimulatedPeople(IEnumerable<PersonInfo> people)
            {
                this.simulatedPeople.Clear();
                foreach (var person in people)
                {
                    this.simulatedPeople.Add(person);
                }
            }

            public void SetSimulatedPeopleCount(int count)
            {
                this.simulatedPeople.Clear();
                for (var i = 0; i < count; i++)
                {
                    this.simulatedPeople.Add(MovieSeriesPeopleRefreshLifecycleTest.CreatePerson((1000 + i).ToString(), i == 0 ? "Actor" : "Director", i == 0 ? "角色A" : "Director", $"Simulated Person {i}"));
                }
            }

            public override ItemUpdateType OnMetadataChanged()
            {
                this.MetadataChangedCallCount++;
                return ItemUpdateType.MetadataEdit;
            }

            private System.Collections.IEnumerable GetPeople()
            {
                return this.simulatedPeople;
            }

            public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
            {
                this.UpdateToRepositoryCallCount++;
                this.LastUpdateReason = updateReason;
                return Task.CompletedTask;
            }
        }

        private sealed class TrackingSeries : Series, ITrackingLifecycleItem
        {
            private readonly List<object> simulatedPeople = new List<object>();

            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public ItemUpdateType? LastUpdateReason { get; private set; }

            public void SetSimulatedPeople(IEnumerable<PersonInfo> people)
            {
                this.simulatedPeople.Clear();
                foreach (var person in people)
                {
                    this.simulatedPeople.Add(person);
                }
            }

            public void SetSimulatedPeopleCount(int count)
            {
                this.simulatedPeople.Clear();
                for (var i = 0; i < count; i++)
                {
                    this.simulatedPeople.Add(MovieSeriesPeopleRefreshLifecycleTest.CreatePerson((2000 + i).ToString(), i == 0 ? "Actor" : "Director", i == 0 ? "角色A" : "Director", $"Simulated Series Person {i}"));
                }
            }

            public override ItemUpdateType OnMetadataChanged()
            {
                this.MetadataChangedCallCount++;
                return ItemUpdateType.MetadataEdit;
            }

            private System.Collections.IEnumerable GetPeople()
            {
                return this.simulatedPeople;
            }

            public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
            {
                this.UpdateToRepositoryCallCount++;
                this.LastUpdateReason = updateReason;
                return Task.CompletedTask;
            }
        }

        private sealed class QueueRefreshInvocation
        {
            public Guid ItemId { get; set; }

            public MetadataRefreshOptions Options { get; set; } = null!;

            public RefreshPriority Priority { get; set; }
        }

        private sealed class ProgressRecorder : IProgress<double>
        {
            public void Report(double value)
            {
            }
        }
    }
}
