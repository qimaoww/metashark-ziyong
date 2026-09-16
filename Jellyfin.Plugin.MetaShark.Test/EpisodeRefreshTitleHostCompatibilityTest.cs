#if JELLYFIN_HOST_TESTS
using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Compatibility;
using Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping;
using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Providers.Manager;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("HostCompatibility")]
    [DoNotParallelize]
    public class EpisodeRefreshTitleHostCompatibilityTest
    {
        private IMediaSourceManager? originalMediaSourceManager;
        private IFileSystem? originalFileSystem;
        private PluginConfiguration? originalConfiguration;

        [TestInitialize]
        public void Initialize()
        {
            this.originalMediaSourceManager = BaseItem.MediaSourceManager;
            var mediaSourceManager = new Mock<IMediaSourceManager>();
            mediaSourceManager.Setup(x => x.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
            BaseItem.MediaSourceManager = mediaSourceManager.Object;
            this.originalFileSystem = BaseItem.FileSystem;
            var fileSystem = new Mock<IFileSystem>();
            fileSystem.Setup(x => x.IsPathFile(It.IsAny<string>())).Returns(true);
            BaseItem.FileSystem = fileSystem.Object;
            this.originalConfiguration = MetaSharkPlugin.Instance?.Configuration;
            ExplicitEpisodeGroupMappingTestHelper.ResetPluginConfiguration();
        }

        [TestCleanup]
        public void Cleanup()
        {
            BaseItem.MediaSourceManager = this.originalMediaSourceManager!;
            BaseItem.FileSystem = this.originalFileSystem!;
            ExplicitEpisodeGroupMappingTestHelper.ReplacePluginConfiguration(this.originalConfiguration ?? new PluginConfiguration());
        }

        [DataTestMethod]
        [DataRow(91768, 4, 18, EpisodeRefreshTitleGuardTest.BookwormEmbeddedTitle)]
        [DataRow(280049, 2, 1, EpisodeRefreshTitleGuardTest.HellModeEmbeddedTitle)]
        public async Task HostWithoutGuard_ReproducesExistingTitleOverwritten(int seriesId, int season, int episode, string embeddedTitle)
        {
            using var harness = new HostHarness(seriesId, season, episode, "已有的正确单集标题", "新的单集刮削标题", embeddedTitle);

            var result = await harness.RunAsync(includeGuard: false);

            Assert.AreEqual(0, result.Failures);
            Assert.AreEqual(embeddedTitle, harness.Probe.TitleAfterProbe);
            Assert.AreEqual(embeddedTitle, harness.Item.Name, "真实 Jellyfin 12 合并会保留已被 probe 覆盖的标题。");
            Assert.AreEqual("单集简介", harness.Item.Overview, "远程刮削成功并不代表标题成功保存。");
        }

        [DataTestMethod]
        [DataRow(91768, 4, 18, "达穆尔的请求", EpisodeRefreshTitleGuardTest.BookwormEmbeddedTitle)]
        [DataRow(280049, 2, 1, "已有的正确单集标题", EpisodeRefreshTitleGuardTest.HellModeEmbeddedTitle)]
        public async Task HostWithGuard_PreservesTitleAndStillFillsMissingOverview(int seriesId, int season, int episode, string originalTitle, string embeddedTitle)
        {
            using var harness = new HostHarness(seriesId, season, episode, originalTitle, "新的单集刮削标题", embeddedTitle);

            var result = await harness.RunAsync();

            Assert.AreEqual(0, result.Failures);
            Assert.AreEqual(embeddedTitle, harness.Probe.TitleAfterProbe, "必须真正执行宿主内嵌标题导入，再验证保护生效。");
            Assert.AreEqual(originalTitle, harness.Item.Name);
            Assert.AreEqual("单集简介", harness.Item.Overview);
            Assert.AreEqual(season, harness.Item.ParentIndexNumber);
            Assert.AreEqual(episode, harness.Item.IndexNumber);
            Assert.IsNull(harness.Candidates.Peek(harness.Item.Id), "保护已有标题不借用默认标题回填。");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task HostWithGuard_DefaultTitleBackfillKeepsExistingFeatureSwitch(bool backfillEnabled)
        {
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = backfillEnabled;
            using var harness = new HostHarness(91768, 4, 18, "第 18 集", "达穆尔的请求", EpisodeRefreshTitleGuardTest.BookwormEmbeddedTitle);

            var result = await harness.RunAsync();

            Assert.AreEqual(0, result.Failures);
            Assert.AreEqual("第 18 集", harness.Item.Name, "宿主合并仍遵守搜索缺失语义；默认标题由原有后处理决定是否回填。");
            Assert.AreEqual(backfillEnabled, harness.Candidates.Peek(harness.Item.Id) != null);

            var persistence = new Mock<IEpisodeTitleBackfillPersistence>();
            persistence.Setup(x => x.SaveAsync(harness.Item, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var postProcess = new EpisodeTitleBackfillPostProcessService(
                harness.Candidates,
                persistence.Object,
                NullLogger<EpisodeTitleBackfillPostProcessService>.Instance);
            await postProcess.TryApplyAsync(
                new ItemChangeEventArgs { Item = harness.Item, UpdateReason = result.UpdateType },
                "ItemUpdated",
                CancellationToken.None);

            Assert.AreEqual(backfillEnabled ? "达穆尔的请求" : "第 18 集", harness.Item.Name);
            persistence.Verify(x => x.SaveAsync(harness.Item, It.IsAny<CancellationToken>()), backfillEnabled ? Times.Once() : Times.Never());
        }

        [TestMethod]
        public async Task HostWithGuard_DefaultTitleWithoutTranslationStaysDefault()
        {
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;
            using var harness = new HostHarness(280049, 2, 1, "第 1 集", "Episode 1", EpisodeRefreshTitleGuardTest.HellModeEmbeddedTitle);

            var result = await harness.RunAsync();

            Assert.AreEqual(0, result.Failures);
            Assert.AreEqual("第 1 集", harness.Item.Name);
            Assert.IsNull(harness.Candidates.Peek(harness.Item.Id));
        }

        [DataTestMethod]
        [DataRow(MetadataRefreshMode.FullRefresh, true)]
        [DataRow(MetadataRefreshMode.Default, false)]
        public async Task HostWithGuard_OverwriteAndAutomaticRefreshAreUnchanged(MetadataRefreshMode mode, bool replaceAll)
        {
            using var harness = new HostHarness(91768, 4, 18, "旧标题", "达穆尔的请求", EpisodeRefreshTitleGuardTest.BookwormEmbeddedTitle);
            harness.Options.MetadataRefreshMode = mode;
            harness.Options.ReplaceAllMetadata = replaceAll;

            var result = await harness.RunAsync();

            Assert.AreEqual(0, result.Failures);
            Assert.AreEqual("达穆尔的请求", harness.Item.Name);
        }

        [TestMethod]
        public async Task HostWithGuard_RemoteFailureDoesNotLoseExistingTitle()
        {
            using var harness = new HostHarness(91768, 4, 18, "达穆尔的请求", "unused", EpisodeRefreshTitleGuardTest.BookwormEmbeddedTitle);
            var failing = new Mock<IRemoteMetadataProvider<Episode, EpisodeInfo>>();
            failing.SetupGet(x => x.Name).Returns("MetaShark");
            failing.Setup(x => x.GetMetadata(It.IsAny<EpisodeInfo>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("offline fixture"));
            harness.RemoteProvider = failing.Object;

            var result = await harness.RunAsync();

            Assert.AreEqual(1, result.Failures);
            Assert.AreEqual("达穆尔的请求", harness.Item.Name);
        }

        [TestMethod]
        public async Task HostWithGuard_MissingSeriesIdDoesNotLoseExistingTitle()
        {
            using var harness = new HostHarness(91768, 4, 18, "达穆尔的请求", "unused", EpisodeRefreshTitleGuardTest.BookwormEmbeddedTitle);
            harness.Info.SeriesProviderIds.Clear();

            var result = await harness.RunAsync();

            Assert.AreEqual(0, result.Failures);
            Assert.AreEqual("达穆尔的请求", harness.Item.Name);
        }

        [TestMethod]
        public async Task HostWithGuard_LockedLocalMetadataStillHasHostPriority()
        {
            using var harness = new HostHarness(91768, 4, 18, "达穆尔的请求", "远程标题", EpisodeRefreshTitleGuardTest.BookwormEmbeddedTitle);
            var local = new Mock<ILocalMetadataProvider<Episode>>();
            local.SetupGet(x => x.Name).Returns("Local NFO");
            local.Setup(x => x.GetMetadata(It.IsAny<ItemInfo>(), It.IsAny<IDirectoryService>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResult<Episode>
                {
                    HasMetadata = true,
                    Item = new Episode { Name = "用户锁定的 NFO 标题", IsLocked = true },
                });
            harness.LocalProvider = local.Object;

            var result = await harness.RunAsync();

            Assert.AreEqual(0, result.Failures);
            Assert.AreEqual("用户锁定的 NFO 标题", harness.Item.Name);
            Assert.IsTrue(harness.Item.IsLocked);
        }

        [TestMethod]
        public async Task HostWithGuard_NameLockIsUnchanged()
        {
            using var harness = new HostHarness(91768, 4, 18, "用户锁定的单集标题", "达穆尔的请求", EpisodeRefreshTitleGuardTest.BookwormEmbeddedTitle);
            harness.Item.LockedFields = [MetadataField.Name];

            var result = await harness.RunAsync();

            Assert.AreEqual(0, result.Failures);
            Assert.AreEqual("用户锁定的单集标题", harness.Item.Name);
            CollectionAssert.AreEqual(new[] { MetadataField.Name }, harness.Item.LockedFields);
        }

        private sealed class HostHarness : IDisposable
        {
            private readonly EpisodeProvider provider;
            private readonly EpisodeRefreshTitleGuard guard;

            public HostHarness(int seriesId, int season, int episode, string originalTitle, string providerTitle, string embeddedTitle)
            {
                this.Item = new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = originalTitle,
                    Path = $"/test/series-{seriesId}/Season {season}/series-{seriesId}.S{season:00}E{episode:00}.mp4",
                    IndexNumber = episode,
                    ParentIndexNumber = season,
                };
                // The host captures this lookup BEFORE running pre-refresh providers.
                this.Info = new EpisodeInfo
                {
                    Name = originalTitle,
                    Path = this.Item.Path,
                    IndexNumber = episode,
                    ParentIndexNumber = season,
                    MetadataLanguage = "zh-CN",
                    SeriesDisplayOrder = string.Empty,
                    SeriesProviderIds = new Dictionary<string, string> { ["Tmdb"] = seriesId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                };
                var libraryManager = new Mock<ILibraryManager>();
                libraryManager.Setup(x => x.FindByPath(this.Item.Path, false)).Returns(this.Item);
                this.guard = EpisodeRefreshTitleGuardTest.CreateGuard(libraryManager: libraryManager);
                var loggerFactory = NullLoggerFactory.Instance;
                var tmdb = new TmdbApi(loggerFactory);
                var cache = (IMemoryCache)typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tmdb)!;
                cache.Set($"episode-{seriesId}-s{season}e{episode}-zh-CN-zh-CN", new TvEpisode { Name = providerTitle, Overview = "单集简介" });
                cache.Set($"episode-translation-overview-{seriesId}-s{season}e{episode}-zh-CN", new EpisodeLocalizedValue { Value = "单集简介", SourceLanguage = "zh-CN" });
                cache.Set($"episode-translation-title-{seriesId}-s{season}e{episode}-zh-CN", (EpisodeLocalizedValue?)null);
                this.provider = new EpisodeProvider(
                    new DefaultHttpClientFactory(),
                    loggerFactory,
                    libraryManager.Object,
                    new HttpContextAccessor(), // Also covers scheduled/recursive refresh without an HTTP request.
                    new DoubanApi(loggerFactory),
                    tmdb,
                    new OmdbApi(loggerFactory),
                    new ImdbApi(loggerFactory),
                    new TvdbApi(loggerFactory),
                    this.Candidates);
                this.RemoteProvider = this.provider;
                this.Probe = new HostEmbeddedTitleProbe(embeddedTitle);
            }

            public Episode Item { get; }

            public EpisodeInfo Info { get; }

            public MetadataRefreshOptions Options { get; } = EpisodeRefreshTitleGuardTest.CreateOptions();

            public InMemoryEpisodeTitleBackfillCandidateStore Candidates { get; } = new();

            public HostEmbeddedTitleProbe Probe { get; }

            public IMetadataProvider RemoteProvider { get; set; }

            public IMetadataProvider? LocalProvider { get; set; }

            public Task<RefreshResult> RunAsync(bool includeGuard = true)
            {
                var providers = new List<IMetadataProvider> { this.RemoteProvider, this.Probe };
                if (includeGuard)
                {
                    providers.Add(new EpisodeTitleSnapshotProvider(this.guard));
                    providers.Add(new EpisodeTitleRestoreProvider(this.guard));
                }

                if (this.LocalProvider != null)
                {
                    providers.Add(this.LocalProvider);
                }

                // The host sorts custom providers by IHasOrder, then runs the IPreRefreshProvider
                // phase before local/remote providers, regardless of their positions in this list.
                providers = providers.OrderBy(x => x is IHasOrder ordered ? ordered.Order : 50).ToList();
                return new HostEpisodeService().RunAsync(this.Item, this.Info, this.Options, providers);
            }

            public void Dispose() => this.provider.Dispose();
        }

        private sealed class HostEmbeddedTitleProbe(string embeddedTitle) : ICustomMetadataProvider<Episode>, IPreRefreshProvider, IHasOrder
        {
            private static readonly Type ProbeType = typeof(MetadataService<,>).Assembly.GetType("MediaBrowser.Providers.MediaInfo.FFProbeVideoInfo", throwOnError: true)!;
            private static readonly MethodInfo FetchEmbeddedInfo = ProbeType.GetMethod("FetchEmbeddedInfo", BindingFlags.Instance | BindingFlags.NonPublic)!;
            private static readonly Type HostProviderType = typeof(MetadataService<,>).Assembly.GetType("MediaBrowser.Providers.MediaInfo.ProbeProvider", throwOnError: true)!;

            public string Name => "Probe Provider";

            public int Order => ((IHasOrder)RuntimeHelpers.GetUninitializedObject(HostProviderType)).Order;

            public string? TitleAfterProbe { get; private set; }

            public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
            {
                // Only this host method is executed. It needs no prober services and touches
                // only the in-memory fixture; no real media, network probe or library write occurs.
                FetchEmbeddedInfo.Invoke(RuntimeHelpers.GetUninitializedObject(ProbeType),
                [
                    item,
                    new MediaInfo { Name = embeddedTitle },
                    options,
                    new LibraryOptions { EnableEmbeddedTitles = true },
                ]);
                this.TitleAfterProbe = item.Name;
                return Task.FromResult(ItemUpdateType.MetadataImport);
            }
        }

        private sealed class HostEpisodeService : MetadataService<Episode, EpisodeInfo>
        {
            public HostEpisodeService()
                : base(
                    Mock.Of<IServerConfigurationManager>(),
                    NullLogger<MetadataService<Episode, EpisodeInfo>>.Instance,
                    Mock.Of<IProviderManager>(),
                    Mock.Of<IFileSystem>(),
                    Mock.Of<ILibraryManager>(),
                    Mock.Of<IExternalDataManager>(),
                    Mock.Of<IItemRepository>())
            {
            }

            public Task<RefreshResult> RunAsync(Episode item, EpisodeInfo info, MetadataRefreshOptions options, ICollection<IMetadataProvider> providers)
                => this.RefreshWithProviders(new MetadataResult<Episode> { Item = item }, info, options, providers, this.ImageProvider, false, CancellationToken.None);
        }
    }
}
#endif
