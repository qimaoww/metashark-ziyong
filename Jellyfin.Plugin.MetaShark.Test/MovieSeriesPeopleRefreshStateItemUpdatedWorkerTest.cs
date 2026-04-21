using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Test.Logging;
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
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class MovieSeriesPeopleRefreshStateItemUpdatedWorkerTest
    {
        [TestMethod]
        public async Task TryApplyAsync_MetadataDownloadMovie_ShouldSaveCurrentStateToStoreWithoutTouchingItemMetadata()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var service = CreatePostProcessService(stateStore);
            var movie = CreateMovie(includeTmdb: true);

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = movie,
                    UpdateReason = ItemUpdateType.MetadataDownload,
                },
                MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            var state = stateStore.GetState(movie.Id);
            Assert.IsNotNull(state);
            Assert.AreEqual(movie.Id, state!.ItemId);
            Assert.AreEqual(nameof(Movie), state.ItemType);
            Assert.AreEqual("123456", state.TmdbId);
            Assert.AreEqual(PeopleRefreshState.CurrentVersion, state.Version);
            Assert.AreNotEqual(default, state.UpdatedAtUtc);
            Assert.AreEqual(1, stateStore.SaveCallCount);
            Assert.AreEqual(0, movie.MetadataChangedCallCount);
            Assert.AreEqual(0, movie.UpdateToRepositoryCallCount);
            Assert.IsNull(movie.LastUpdateReason);
            Assert.IsFalse(movie.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false);
        }

        [TestMethod]
        public async Task TryApplyAsync_RejectedUpdateReason_ShouldSkipWithoutPersisting()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var service = CreatePostProcessService(stateStore);
            var series = CreateSeries(includeTmdb: true);

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = series,
                    UpdateReason = ItemUpdateType.ImageUpdate,
                },
                MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, series.MetadataChangedCallCount);
            Assert.AreEqual(0, series.UpdateToRepositoryCallCount);
            Assert.AreEqual(0, stateStore.SaveCallCount);
            Assert.IsNull(stateStore.GetState(series.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_UnsupportedType_ShouldSkipWithoutPersisting()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var service = CreatePostProcessService(stateStore);
            var episode = CreateEpisode(includeTmdb: true);

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = episode,
                    UpdateReason = ItemUpdateType.MetadataImport,
                },
                MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, episode.MetadataChangedCallCount);
            Assert.AreEqual(0, episode.UpdateToRepositoryCallCount);
            Assert.AreEqual(0, stateStore.SaveCallCount);
            Assert.IsNull(stateStore.GetState(episode.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_MissingTmdbProviderId_ShouldSkipWithoutPersisting()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var service = CreatePostProcessService(stateStore);
            var movie = CreateMovie(includeTmdb: false);

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = movie,
                    UpdateReason = ItemUpdateType.MetadataImport,
                },
                MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, movie.MetadataChangedCallCount);
            Assert.AreEqual(0, movie.UpdateToRepositoryCallCount);
            Assert.AreEqual(0, stateStore.SaveCallCount);
            Assert.IsNull(stateStore.GetState(movie.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_CurrentStateAlreadyCurrent_ShouldNotPersistAgain()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var service = CreatePostProcessService(stateStore);
            var series = CreateSeries(includeTmdb: true);
            PeopleRefreshStateTestHelper.SaveState(stateStore, series, PeopleRefreshState.CurrentVersion);
            var originalSaveCallCount = stateStore.SaveCallCount;

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = series,
                    UpdateReason = ItemUpdateType.MetadataImport,
                },
                MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            var state = stateStore.GetState(series.Id);
            Assert.IsNotNull(state);
            Assert.AreEqual(PeopleRefreshState.CurrentVersion, state!.Version);
            Assert.AreEqual(originalSaveCallCount, stateStore.SaveCallCount);
            Assert.AreEqual(0, series.MetadataChangedCallCount);
            Assert.AreEqual(0, series.UpdateToRepositoryCallCount);
        }

        [TestMethod]
        public async Task TryApplyAsync_MetadataDownloadWithSparsePeopleCandidate_ShouldQueueOverwriteAndKeepPendingCandidate()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var candidateStore = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var queued = new List<(Guid ItemId, MetadataRefreshOptions Options, RefreshPriority Priority)>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) => queued.Add((itemId, options, priority)));
            var service = CreatePostProcessService(stateStore, providerManagerStub.Object, candidateStore);
            var movie = CreateMovie(includeTmdb: true);
            candidateStore.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = movie.Id,
                ItemPath = movie.Path,
                ExpectedPeopleCount = 2,
            });

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = movie,
                    UpdateReason = ItemUpdateType.MetadataDownload,
                },
                MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual(movie.Id, queued[0].ItemId);
            Assert.IsTrue(queued[0].Options.ReplaceAllMetadata);
            Assert.AreEqual(0, stateStore.SaveCallCount);
            Assert.IsNull(stateStore.GetState(movie.Id));
            var pendingCandidate = candidateStore.Peek(movie.Id);
            Assert.IsNotNull(pendingCandidate);
            Assert.AreEqual(2, pendingCandidate!.ExpectedPeopleCount);
            Assert.IsTrue(pendingCandidate.OverwriteQueued);
        }

        [TestMethod]
        public async Task TryApplyAsync_MetadataDownloadWithAlreadyQueuedSparsePeopleCandidate_ShouldNotRequeueAndShouldStayPending()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var candidateStore = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var queued = new List<(Guid ItemId, MetadataRefreshOptions Options, RefreshPriority Priority)>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) => queued.Add((itemId, options, priority)));
            var service = CreatePostProcessService(stateStore, providerManagerStub.Object, candidateStore);
            var movie = CreateMovie(includeTmdb: true);
            candidateStore.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = movie.Id,
                ItemPath = movie.Path,
                ExpectedPeopleCount = 2,
                OverwriteQueued = true,
            });

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = movie,
                    UpdateReason = ItemUpdateType.MetadataDownload,
                },
                MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, queued.Count);
            Assert.AreEqual(0, stateStore.SaveCallCount);
            Assert.IsNull(stateStore.GetState(movie.Id));
            var pendingCandidate = candidateStore.Peek(movie.Id);
            Assert.IsNotNull(pendingCandidate);
            Assert.AreEqual(2, pendingCandidate!.ExpectedPeopleCount);
            Assert.IsTrue(pendingCandidate.OverwriteQueued);
        }

        [TestMethod]
        public async Task TryApplyAsync_MetadataDownloadWithSatisfiedQueuedPeopleCandidate_ShouldNotQueueOverwriteAndShouldPersistState()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var candidateStore = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var service = CreatePostProcessService(stateStore, providerManagerStub.Object, candidateStore);
            var series = CreateSeries(includeTmdb: true);
            series.SetSimulatedPeopleCount(2);
            candidateStore.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = series.Id,
                ItemPath = series.Path,
                ExpectedPeopleCount = 2,
                OverwriteQueued = true,
            });

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = series,
                    UpdateReason = ItemUpdateType.MetadataDownload,
                },
                MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
            Assert.AreEqual(1, stateStore.SaveCallCount);
            Assert.IsNotNull(stateStore.GetState(series.Id));
            Assert.IsNull(candidateStore.Peek(series.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_MetadataDownloadWithQueueFailure_ShouldRestoreUnqueuedCandidate()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var providerManagerStub = new Mock<IProviderManager>();
            var candidateStore = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Throws(new InvalidOperationException("queue failed"));
            var service = CreatePostProcessService(stateStore, providerManagerStub.Object, candidateStore);
            var movie = CreateMovie(includeTmdb: true);
            candidateStore.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = movie.Id,
                ItemPath = movie.Path,
                ExpectedPeopleCount = 2,
            });

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await service.TryApplyAsync(
                    new ItemChangeEventArgs
                    {
                        Item = movie,
                        UpdateReason = ItemUpdateType.MetadataDownload,
                    },
                    MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(0, stateStore.SaveCallCount);
            Assert.IsNull(stateStore.GetState(movie.Id));
            var restoredCandidate = candidateStore.Peek(movie.Id);
            Assert.IsNotNull(restoredCandidate);
            Assert.AreEqual(2, restoredCandidate!.ExpectedPeopleCount);
            Assert.IsFalse(restoredCandidate.OverwriteQueued, "排队失败时 candidate 不应误标记成已排队。 ");
        }

        [TestMethod]
        public async Task TryApplyAsync_CurrentStateWithLegacyProviderIdResidue_ShouldRemoveResidueAndPersistMetadataEditWithoutSavingStoreAgain()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var service = CreatePostProcessService(stateStore);
            var series = CreateSeries(includeTmdb: true);
            AddLegacyPeopleRefreshStateProviderId(series, "tmdb-people-strict-zh-cn-v1");
            PeopleRefreshStateTestHelper.SaveState(stateStore, series, PeopleRefreshState.CurrentVersion);
            var originalSaveCallCount = stateStore.SaveCallCount;

            await service.TryApplyAsync(
                new ItemChangeEventArgs
                {
                    Item = series,
                    UpdateReason = ItemUpdateType.MetadataImport,
                },
                MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                CancellationToken.None).ConfigureAwait(false);

            var state = stateStore.GetState(series.Id);
            Assert.IsNotNull(state);
            Assert.AreEqual(PeopleRefreshState.CurrentVersion, state!.Version);
            Assert.AreEqual(originalSaveCallCount, stateStore.SaveCallCount);
            Assert.AreEqual(1, series.MetadataChangedCallCount);
            Assert.AreEqual(1, series.UpdateToRepositoryCallCount);
            Assert.AreEqual(ItemUpdateType.MetadataEdit, series.LastUpdateReason);
            Assert.IsFalse(series.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false);
            Assert.AreEqual(1, series.ProviderIds?.Count ?? 0);
            CollectionAssert.AreEquivalent(new[] { MetadataProvider.Tmdb.ToString() }, new List<string>(series.ProviderIds!.Keys));
        }

        [TestMethod]
        public async Task TryApplyAsync_CurrentStateWithDirtySeriesNfo_ShouldStripLegacyTagWithoutReintroducingProviderIdPersistence()
        {
            var stateStore = new TestPeopleRefreshStateStore();
            var service = CreatePostProcessService(stateStore);
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-nfo-cleanup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);

            try
            {
                var series = CreateSeries(includeTmdb: true);
                series.Path = tempRoot;
                var nfoPath = Path.Combine(tempRoot, "tvshow.nfo");
                File.WriteAllText(
                    nfoPath,
                    "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<tvshow>\n  <title>Series A</title>\n  <metasharkpeoplerefreshstateid>tmdb-people-strict-zh-cn-v1</metasharkpeoplerefreshstateid>\n  <tmdbid>123456</tmdbid>\n</tvshow>\n");
                PeopleRefreshStateTestHelper.SaveState(stateStore, series, PeopleRefreshState.CurrentVersion);
                var originalSaveCallCount = stateStore.SaveCallCount;

                await service.TryApplyAsync(
                    new ItemChangeEventArgs
                    {
                        Item = series,
                        UpdateReason = ItemUpdateType.MetadataDownload,
                    },
                    MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger,
                    CancellationToken.None).ConfigureAwait(false);

                var state = stateStore.GetState(series.Id);
                Assert.IsNotNull(state);
                Assert.AreEqual(PeopleRefreshState.CurrentVersion, state!.Version);
                Assert.AreEqual(originalSaveCallCount, stateStore.SaveCallCount);
                Assert.AreEqual(0, series.MetadataChangedCallCount);
                Assert.AreEqual(0, series.UpdateToRepositoryCallCount);
                Assert.IsNull(series.LastUpdateReason);
                Assert.IsFalse(series.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false);

                var nfo = File.ReadAllText(nfoPath);
                Assert.IsFalse(nfo.Contains("metasharkpeoplerefreshstateid", StringComparison.OrdinalIgnoreCase));
                StringAssert.Contains(nfo, "<title>Series A</title>");
                StringAssert.Contains(nfo, "<tmdbid>123456</tmdbid>");
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [TestMethod]
        public async Task StartAsync_ForwardsItemUpdatedEventToPostProcessServiceAndLogsStructuredMessage()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            var postProcessServiceStub = new RecordingMovieSeriesPeopleRefreshStatePostProcessService();
            var loggerStub = new Mock<ILogger<MovieSeriesPeopleRefreshStateItemUpdatedWorker>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var worker = new MovieSeriesPeopleRefreshStateItemUpdatedWorker(libraryManagerStub.Object, postProcessServiceStub, loggerStub.Object);

            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var movie = CreateMovie(includeTmdb: true);
            libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = movie,
                    UpdateReason = ItemUpdateType.MetadataImport,
                });

            Assert.AreEqual(1, postProcessServiceStub.CallCount);
            Assert.AreSame(movie, postProcessServiceStub.LastEventArgs?.Item);
            Assert.AreEqual(ItemUpdateType.MetadataImport, postProcessServiceStub.LastEventArgs?.UpdateReason);
            Assert.AreEqual(MovieSeriesPeopleRefreshStatePostProcessService.ItemUpdatedTrigger, postProcessServiceStub.LastTriggerName);
            Assert.AreEqual(CancellationToken.None, postProcessServiceStub.LastCancellationToken);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Name"] = movie.Name,
                    ["Id"] = movie.Id,
                    ["ItemPath"] = movie.Path,
                    ["UpdateReason"] = ItemUpdateType.MetadataImport,
                },
                originalFormatContains: "[MetaShark] 收到影视人物刷新状态条目更新事件",
                messageContains: ["[MetaShark] 收到影视人物刷新状态条目更新事件", "trigger=ItemUpdated", $"itemId={movie.Id}", $"itemPath={movie.Path}"]);
        }

        [TestMethod]
        public async Task StartAsync_LogsAndRethrows_WhenPostProcessThrows()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            var postProcessServiceStub = new RecordingMovieSeriesPeopleRefreshStatePostProcessService
            {
                ExceptionToThrow = new InvalidOperationException("postprocess boom"),
            };
            var loggerStub = new Mock<ILogger<MovieSeriesPeopleRefreshStateItemUpdatedWorker>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var worker = new MovieSeriesPeopleRefreshStateItemUpdatedWorker(libraryManagerStub.Object, postProcessServiceStub, loggerStub.Object);

            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var series = CreateSeries(includeTmdb: true);
            var actualException = Assert.ThrowsException<InvalidOperationException>(() => libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = series,
                    UpdateReason = ItemUpdateType.MetadataDownload,
                }));

            Assert.AreSame(postProcessServiceStub.ExceptionToThrow, actualException);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Error,
                expectException: true,
                stateContains: new Dictionary<string, object?>
                {
                    ["Id"] = series.Id,
                    ["ItemPath"] = series.Path,
                    ["UpdateReason"] = ItemUpdateType.MetadataDownload,
                },
                originalFormatContains: "[MetaShark] 影视人物刷新状态后处理失败",
                messageContains: ["[MetaShark] 影视人物刷新状态后处理失败", "trigger=ItemUpdated", $"itemId={series.Id}", $"itemPath={series.Path}"]);
        }

        private static MovieSeriesPeopleRefreshStatePostProcessService CreatePostProcessService(IPeopleRefreshStateStore? stateStore = null, IProviderManager? providerManager = null, IMovieSeriesPeopleOverwriteRefreshCandidateStore? overwriteRefreshCandidateStore = null)
        {
            return new MovieSeriesPeopleRefreshStatePostProcessService(
                new Mock<ILogger<MovieSeriesPeopleRefreshStatePostProcessService>>().Object,
                stateStore ?? new TestPeopleRefreshStateStore(),
                providerManager,
                overwriteRefreshCandidateStore,
                new Mock<IFileSystem>().Object);
        }

        private static TrackingMovie CreateMovie(bool includeTmdb)
        {
            var movie = new TrackingMovie
            {
                Id = Guid.NewGuid(),
                Name = "Movie A",
                Path = "/library/movies/movie-a/movie-a.mkv",
            };

            ConfigureProviderIds(movie, includeTmdb);
            return movie;
        }

        private static TrackingSeries CreateSeries(bool includeTmdb)
        {
            var series = new TrackingSeries
            {
                Id = Guid.NewGuid(),
                Name = "Series A",
                Path = "/library/tv/series-a",
            };

            ConfigureProviderIds(series, includeTmdb);
            return series;
        }

        private static TrackingEpisode CreateEpisode(bool includeTmdb)
        {
            var episode = new TrackingEpisode
            {
                Id = Guid.NewGuid(),
                Name = "Episode A",
                Path = "/library/tv/series-a/Season 01/episode-a.mkv",
            };

            ConfigureProviderIds(episode, includeTmdb);
            return episode;
        }

        private static void ConfigureProviderIds(MediaBrowser.Controller.Entities.BaseItem item, bool includeTmdb)
        {
            if (includeTmdb)
            {
                item.SetProviderId(MetadataProvider.Tmdb, "123456");
            }
        }

        private static void AddLegacyPeopleRefreshStateProviderId(MediaBrowser.Controller.Entities.BaseItem item, string version)
        {
            Assert.IsNotNull(item.ProviderIds);
            item.ProviderIds!["MetaSharkPeopleRefreshState"] = version;
        }

        private sealed class TrackingMovie : Movie
        {
            private readonly List<object> simulatedPeople = new List<object>();

            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public ItemUpdateType? LastUpdateReason { get; private set; }

            public void SetSimulatedPeopleCount(int count)
            {
                this.simulatedPeople.Clear();
                for (var i = 0; i < count; i++)
                {
                    this.simulatedPeople.Add(new object());
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

        private sealed class TrackingSeries : Series
        {
            private readonly List<object> simulatedPeople = new List<object>();

            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public ItemUpdateType? LastUpdateReason { get; private set; }

            public void SetSimulatedPeopleCount(int count)
            {
                this.simulatedPeople.Clear();
                for (var i = 0; i < count; i++)
                {
                    this.simulatedPeople.Add(new object());
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

        private sealed class TrackingEpisode : Episode
        {
            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public override ItemUpdateType OnMetadataChanged()
            {
                this.MetadataChangedCallCount++;
                return ItemUpdateType.MetadataEdit;
            }

            public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
            {
                this.UpdateToRepositoryCallCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingMovieSeriesPeopleRefreshStatePostProcessService : MovieSeriesPeopleRefreshStatePostProcessService
        {
            public RecordingMovieSeriesPeopleRefreshStatePostProcessService()
                : base(new Mock<ILogger<MovieSeriesPeopleRefreshStatePostProcessService>>().Object, new TestPeopleRefreshStateStore())
            {
            }

            public int CallCount { get; private set; }

            public ItemChangeEventArgs? LastEventArgs { get; private set; }

            public string? LastTriggerName { get; private set; }

            public CancellationToken LastCancellationToken { get; private set; }

            public Exception? ExceptionToThrow { get; set; }

            public override Task TryApplyAsync(ItemChangeEventArgs e, string triggerName, CancellationToken cancellationToken)
            {
                this.CallCount++;
                this.LastEventArgs = e;
                this.LastTriggerName = triggerName;
                this.LastCancellationToken = cancellationToken;

                return this.ExceptionToThrow is null
                    ? Task.CompletedTask
                    : Task.FromException(this.ExceptionToThrow);
            }
        }
    }
}
