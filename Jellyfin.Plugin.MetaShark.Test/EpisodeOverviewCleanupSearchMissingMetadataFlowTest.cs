using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeOverviewCleanupSearchMissingMetadataFlowTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestMethod]
        public void ShouldQueueOverviewCleanup_WhenProviderTimeOverviewSnapshotIsAlreadyEmpty()
        {
            var episodeId = Guid.NewGuid();

            var reason = EpisodeProvider.ResolveSearchMissingMetadataOverviewCleanupReason(
                true,
                episodeId,
                string.Empty,
                null);

            Assert.AreEqual(EpisodeProvider.SearchMissingMetadataOverviewCleanupReason.CandidateQueued, reason);
            Assert.IsTrue(
                EpisodeProvider.ShouldQueueSearchMissingMetadataOverviewCleanup(
                    episodeId,
                    string.Empty,
                    null),
                "真实复现里 provider 调用阶段看到的 current overview 已经为空，此时仍必须入队 cleanup candidate。 ");
        }

        [TestMethod]
        public void ShouldNotQueueOverviewCleanup_WhenProviderTimeOverviewSnapshotWasNonEmpty()
        {
            var episodeId = Guid.NewGuid();

            var reason = EpisodeProvider.ResolveSearchMissingMetadataOverviewCleanupReason(
                true,
                episodeId,
                "这是 provider 调用前就存在的合法简介",
                null);

            Assert.AreNotEqual(EpisodeProvider.SearchMissingMetadataOverviewCleanupReason.CandidateQueued, reason);
            Assert.IsFalse(
                EpisodeProvider.ShouldQueueSearchMissingMetadataOverviewCleanup(
                    episodeId,
                    "这是 provider 调用前就存在的合法简介",
                    null),
                "provider-time original overview 仍非空时，不能仅因 provider 本轮没拿到可信 episode overview 就入队 cleanup candidate。 ");
        }

        [TestMethod]
        public void ShouldTreatOriginalOverviewSnapshotAsEmpty_WhenPersistedNfoPlotWasAlreadyCleared()
        {
            var tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"metashark-overview-cleanup-{Guid.NewGuid():N}"));
            try
            {
                var episodePath = Path.Combine(tempRoot.FullName, "episode-01.mkv");
                File.WriteAllText(episodePath, string.Empty);
                File.WriteAllText(Path.ChangeExtension(episodePath, ".nfo"), "<episodedetails><plot></plot></episodedetails>");

                var method = typeof(EpisodeProvider).GetMethod(
                    "ResolveSearchMissingMetadataOverviewCleanupOriginalSnapshot",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.IsNotNull(method, "EpisodeProvider.ResolveSearchMissingMetadataOverviewCleanupOriginalSnapshot 未定义");

                var effectiveSnapshot = method!.Invoke(null, new object?[] { "内存里还挂着的旧简介", episodePath }) as string;
                var reason = EpisodeProvider.ResolveSearchMissingMetadataOverviewCleanupReason(
                    true,
                    Guid.NewGuid(),
                    effectiveSnapshot,
                    null);

                Assert.AreEqual(string.Empty, effectiveSnapshot);
                Assert.AreEqual(EpisodeProvider.SearchMissingMetadataOverviewCleanupReason.CandidateQueued, reason);
            }
            finally
            {
                tempRoot.Delete(recursive: true);
            }
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_MetadataDownloadWithQueuedCleanupCandidate_ClearsStaleEpisodeOverview()
        {
            using var harness = CreateHarness(metadataRefreshMode: "FullRefresh", replaceAllMetadata: "false");

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidate = harness.CandidateStore.Peek(harness.Episode.Id);
            harness.Episode.Overview = "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。";

            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual(null, result.Item!.Overview, "provider 未拿到可信 episode overview 时，本轮返回仍应为空。 ");
            Assert.IsNotNull(queuedCandidate, "search-missing 且 provider 未拿到可信 overview 时应入队 cleanup candidate。");
            Assert.AreEqual(string.Empty, queuedCandidate!.OriginalOverviewSnapshot, "真实复现里 provider 看到的当前 item overview 已经先被清空。 ");
            Assert.AreEqual(1, harness.Persistence.SaveCallCount, "MetadataDownload 应驱动一次 overview cleanup 持久化。");
            Assert.AreEqual(null, harness.Episode.Overview, "refresh 完成后应清掉错误复活的旧 overview。");
            Assert.IsNull(harness.CandidateStore.Peek(harness.Episode.Id), "成功清理后 candidate 应被移除。");
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_TrustedEpisodeOverview_DoesNotQueueCleanupCandidate()
        {
            using var harness = CreateHarness(metadataRefreshMode: "FullRefresh", replaceAllMetadata: "false", translationOverview: "雫第一次参加神之水滴选拔挑战。");

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            harness.Episode.Overview = "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。";

            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("雫第一次参加神之水滴选拔挑战。", result.Item!.Overview);
            Assert.IsNull(harness.CandidateStore.Peek(harness.Episode.Id), "provider 已拿到可信 overview 时不应入队 cleanup candidate。 ");
            Assert.AreEqual(0, harness.Persistence.SaveCallCount, "没有 cleanup candidate 时不应触发 overview 清理持久化。");
            Assert.AreEqual("世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。", harness.Episode.Overview);
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_ProviderTimeOriginalOverviewStillNonEmpty_DoesNotQueueOrApplyCleanup()
        {
            using var harness = CreateHarness(metadataRefreshMode: "FullRefresh", replaceAllMetadata: "false");
            harness.Episode.Overview = "provider 调用前就存在的合法简介";

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual(null, result.Item!.Overview, "provider 本轮未拿到可信 episode overview 时，返回仍应为空。 ");
            Assert.IsNull(harness.CandidateStore.Peek(harness.Episode.Id), "provider-time original overview 非空时不应入队 cleanup candidate。 ");
            Assert.AreEqual(0, harness.Persistence.SaveCallCount, "没有 cleanup candidate 时不应触发 overview 清理持久化。 ");
            Assert.AreEqual("provider 调用前就存在的合法简介", harness.Episode.Overview, "既有非空 overview 不能被过宽的 cleanup 规则误清。 ");
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_NoHttpContextAndCurrentTitleAlreadyBackfilled_StillQueuesAndAppliesCleanup()
        {
            using var harness = CreateHarness(metadataRefreshMode: "FullRefresh", replaceAllMetadata: "false");
            harness.Episode.Name = "勇者的肋骨";
            ((HttpContextAccessor)harness.HttpContextAccessor).HttpContext = null;

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidate = harness.CandidateStore.Peek(harness.Episode.Id);
            harness.Episode.Overview = "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。";

            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual(null, result.Item!.Overview, "provider 未拿到可信 episode overview 时，本轮返回仍应为空。 ");
            Assert.IsNotNull(queuedCandidate, "即使 HttpContext 缺失且当前标题已被回填成真实标题，只要 provider-time original overview 已空且 provider 仍无可信 overview，也必须继续入队 cleanup candidate。 ");
            Assert.AreEqual(string.Empty, queuedCandidate!.OriginalOverviewSnapshot);
            Assert.AreEqual(1, harness.Persistence.SaveCallCount, "缺少 HttpContext 不应阻断真实复现链路上的 overview cleanup 持久化。 ");
            Assert.AreEqual(null, harness.Episode.Overview, "refresh 后错误复活的旧 overview 仍应被清掉。 ");
            Assert.IsNull(harness.CandidateStore.Peek(harness.Episode.Id));
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_MetadataEditOnly_DoesNotApplyCleanup()
        {
            using var harness = CreateHarness(metadataRefreshMode: "FullRefresh", replaceAllMetadata: "false");

            _ = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidate = harness.CandidateStore.Peek(harness.Episode.Id);
            harness.Episode.Overview = "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。";

            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataEdit).ConfigureAwait(false);

            Assert.IsNotNull(queuedCandidate, "成功入队是验证 MetadataEdit-only no-op 的前提。 ");
            Assert.IsNotNull(harness.CandidateStore.Peek(harness.Episode.Id), "MetadataEdit-only 不应消耗 cleanup candidate。 ");
            Assert.AreEqual(0, harness.Persistence.SaveCallCount);
            Assert.AreEqual("世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。", harness.Episode.Overview);
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_ReplaceAllMetadataTrue_DoesNotQueueCleanupCandidate()
        {
            using var harness = CreateHarness(metadataRefreshMode: "FullRefresh", replaceAllMetadata: "true");

            _ = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            harness.Episode.Overview = "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。";
            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNull(harness.CandidateStore.Peek(harness.Episode.Id), "ReplaceAllMetadata=true 时不应入队 cleanup candidate。 ");
            Assert.AreEqual(0, harness.Persistence.SaveCallCount);
            Assert.AreEqual("世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。", harness.Episode.Overview);
        }

        private FlowHarness CreateHarness(string metadataRefreshMode, string replaceAllMetadata, string? translationOverview = "   ")
        {
            return new FlowHarness(this.loggerFactory, metadataRefreshMode, replaceAllMetadata, translationOverview);
        }

        private static EpisodeProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IEpisodeOverviewCleanupCandidateStore overviewCleanupCandidateStore,
            ILoggerFactory loggerFactory)
        {
            return new EpisodeProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager,
                httpContextAccessor,
                new DoubanApi(loggerFactory),
                tmdbApi,
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new TvdbApi(loggerFactory),
                null,
                overviewCleanupCandidateStore);
        }

        private static DefaultHttpContext CreateHttpContext(string metadataRefreshMode, string replaceAllMetadata)
        {
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString($"?metadataRefreshMode={metadataRefreshMode}&replaceAllMetadata={replaceAllMetadata}");
            return context;
        }

        private static EpisodeInfo CreateEpisodeInfo()
        {
            return new EpisodeInfo
            {
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
                MetadataLanguage = "zh-CN",
                ParentIndexNumber = 1,
                IndexNumber = 1,
                SeriesDisplayOrder = string.Empty,
                SeriesProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "123",
                },
            };
        }

        private static void SeedEpisode(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, string imageLanguages, TvEpisode episode)
        {
            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField, "TmdbApi.memoryCache 未找到");

            var cache = cacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(cache, "TmdbApi.memoryCache 不是有效的 MemoryCache");

            var key = $"episode-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}-{imageLanguages}";
            cache!.Set(key, episode);
        }

        private static void SeedEpisodeTranslationOverview(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, string? overview)
        {
            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField, "TmdbApi.memoryCache 未找到");

            var cache = cacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(cache, "TmdbApi.memoryCache 不是有效的 MemoryCache");

            var key = $"episode-translation-overview-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}";
            cache!.Set(
                key,
                overview == null
                    ? null
                    : new EpisodeLocalizedValue
                    {
                        Value = overview,
                        SourceLanguage = language,
                    });
        }

        private sealed class FlowHarness : IDisposable
        {
            private readonly EpisodeOverviewCleanupItemUpdatedWorker worker;
            private bool workerStarted;

            public FlowHarness(ILoggerFactory loggerFactory, string metadataRefreshMode, string replaceAllMetadata, string? translationOverview)
            {
                this.CandidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
                this.Persistence = new RecordingEpisodeOverviewCleanupPersistence();
                this.Info = CreateEpisodeInfo();
                this.Episode = new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = this.Info.Name,
                    Path = this.Info.Path,
                    Overview = null,
                };

                this.LibraryManagerStub = new Mock<ILibraryManager>();
                this.LibraryManagerStub
                    .Setup(x => x.FindByPath(this.Info.Path, false))
                    .Returns(this.Episode);

                this.HttpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = CreateHttpContext(metadataRefreshMode, replaceAllMetadata),
                };

                var tmdbApi = new TmdbApi(loggerFactory);
                SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
                {
                    Name = "Pilot",
                    Overview = null,
                });
                SeedEpisodeTranslationOverview(tmdbApi, 123, 1, 1, "zh-CN", translationOverview);

                this.Provider = CreateProvider(this.LibraryManagerStub.Object, this.HttpContextAccessor, tmdbApi, this.CandidateStore, loggerFactory);
                var postProcessService = new EpisodeOverviewCleanupPostProcessService(
                    this.CandidateStore,
                    this.Persistence,
                    loggerFactory.CreateLogger<EpisodeOverviewCleanupPostProcessService>());
                this.worker = new EpisodeOverviewCleanupItemUpdatedWorker(
                    this.LibraryManagerStub.Object,
                    postProcessService,
                    loggerFactory.CreateLogger<EpisodeOverviewCleanupItemUpdatedWorker>());
            }

            public InMemoryEpisodeOverviewCleanupCandidateStore CandidateStore { get; }

            public RecordingEpisodeOverviewCleanupPersistence Persistence { get; }

            public EpisodeInfo Info { get; }

            public Episode Episode { get; }

            public IHttpContextAccessor HttpContextAccessor { get; }

            public Mock<ILibraryManager> LibraryManagerStub { get; }

            public EpisodeProvider Provider { get; }

            public async Task TriggerItemUpdatedAsync(ItemUpdateType updateReason)
            {
                if (!this.workerStarted)
                {
                    await this.worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    this.workerStarted = true;
                }

                this.LibraryManagerStub.Raise(
                    x => x.ItemUpdated += null,
                    this.LibraryManagerStub.Object,
                    new ItemChangeEventArgs
                    {
                        Item = this.Episode,
                        UpdateReason = updateReason,
                    });
            }

            public void Dispose()
            {
                if (this.workerStarted)
                {
                    this.worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }

                this.Provider.Dispose();
            }
        }

        private sealed class RecordingEpisodeOverviewCleanupPersistence : IEpisodeOverviewCleanupPersistence
        {
            public List<Episode> SavedEpisodes { get; } = new List<Episode>();

            public int SaveCallCount => this.SavedEpisodes.Count;

            public Task SaveAsync(Episode episode, CancellationToken cancellationToken)
            {
                this.SavedEpisodes.Add(episode);
                return Task.CompletedTask;
            }
        }
    }
}
