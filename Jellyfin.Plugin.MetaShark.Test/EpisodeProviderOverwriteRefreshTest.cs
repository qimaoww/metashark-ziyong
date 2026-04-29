using System.Reflection;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeProviderOverwriteRefreshTest
    {
        private const string OverwriteClassifierTypeName = "Jellyfin.Plugin.MetaShark.Providers.OverwriteMetadataRefreshClassifier";
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-overwrite-refresh-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestCleanup]
        public void Cleanup()
        {
            EnsurePluginInstance();
            var configuration = MetaSharkPlugin.Instance!.Configuration;
            configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeDefault;
            configuration.EnableTmdb = true;
            configuration.EnableTmdbMatch = true;
            configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = false;
        }

        [TestMethod]
        public async Task GetMetadata_ShouldKeepEpisodeTitleOverviewPersistencePolicy_WhenReplaceAllMetadataTrue()
        {
            using var harness = CreateHarness(
                PluginConfiguration.DefaultScraperModeTmdbOnly,
                replaceAllMetadata: true,
                metadataLanguage: "en",
                tmdbEpisodeTitle: "TMDb Overwrite Pilot",
                tmdbEpisodeOverview: "TMDb overwrite overview from the episode endpoint.",
                doubanApi: CreateThrowingDoubanApi(this.loggerFactory, "Episode tmdb-only overwrite refresh should not access Douban."));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item, "tmdb-only + overwrite refresh 应继续走 EpisodeProvider 既有 TMDb 单集路径。 ");
            Assert.IsTrue(result.HasMetadata);
            Assert.IsNull(harness.TitleBackfillStore.Peek(harness.Episode.Id), "replaceAllMetadata=true 不应排队 search-missing 标题回填 candidate。 ");
            Assert.IsNull(harness.OverviewCleanupStore.Peek(harness.Episode.Id), "replaceAllMetadata=true 不应排队 search-missing 简介清理 candidate。 ");
            Assert.IsNull(result.Item!.GetProviderId(BaseProvider.DoubanProviderId), "Episode 单集覆盖刷新不应写入或沿用 Douban provider id。 ");
            Assert.AreEqual("第 1 集", result.Item.Name, "replaceAllMetadata=true 不应绕开既有标题持久化策略；非 zh-CN TMDb 标题不能强制覆盖默认单集标题。 ");
            Assert.IsNull(result.Item.Overview, "replaceAllMetadata=true 不应绕开既有简介持久化策略；非 zh-CN TMDb 简介不能强制写回。 ");
            Assert.AreEqual(new DateTime(2024, 6, 8), result.Item.PremiereDate);
            Assert.AreEqual(2024, result.Item.ProductionYear);
            Assert.AreEqual(8.7f, result.Item.CommunityRating);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldRecognizeOverwriteRefreshWithoutChangingEpisodePersistencePolicy()
        {
            using var harness = CreateHarness(
                PluginConfiguration.DefaultScraperModeDefault,
                replaceAllMetadata: true,
                metadataLanguage: "zh-CN",
                tmdbEpisodeTitle: "默认覆盖刷新单集",
                tmdbEpisodeOverview: "默认模式下 Episode 仍只验证单集 TMDb 写回，不改变 Movie/Series 默认语义。");

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("默认覆盖刷新单集", result.Item!.Name, "zh-CN TMDb 标题被接受来自既有 Episode 标题持久化策略，而不是覆盖刷新特例。 ");
            Assert.AreEqual("默认模式下 Episode 仍只验证单集 TMDb 写回，不改变 Movie/Series 默认语义。", result.Item.Overview, "zh-CN TMDb 简介被接受来自既有 Episode 简介持久化策略，而不是覆盖刷新特例。 ");
            Assert.AreEqual(new DateTime(2024, 6, 8), result.Item.PremiereDate);
            Assert.AreEqual(8.7f, result.Item.CommunityRating);
            Assert.IsNull(harness.TitleBackfillStore.Peek(harness.Episode.Id), "default + replaceAllMetadata=true 不应进入标题 search-missing candidate 队列。 ");
            Assert.IsNull(harness.OverviewCleanupStore.Peek(harness.Episode.Id), "default + replaceAllMetadata=true 不应进入简介 search-missing cleanup 队列。 ");
            Assert.IsTrue(
                InvokeIsOverwriteMetadataRefresh(harness.HttpContextAccessor.HttpContext, harness.Episode.Id),
                "Episode 覆盖刷新应复用共享 OverwriteMetadataRefreshClassifier 识别 POST /Items/{id}/Refresh?replaceAllMetadata=true。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldRemainSearchMissingBackfillFlow_WhenFullRefreshWithoutReplaceAllMetadata()
        {
            Assert.IsTrue(
                EpisodeTitleBackfillRefreshClassifier.IsSearchMissingMetadataRefresh("FullRefresh", "false"),
                "FullRefresh + replaceAllMetadata=false 必须继续由 EpisodeTitleBackfillRefreshClassifier 识别为 search-missing。 ");

            using var harness = CreateHarness(
                PluginConfiguration.DefaultScraperModeDefault,
                replaceAllMetadata: false,
                metadataLanguage: "zh-CN",
                tmdbEpisodeTitle: "搜索缺失回填单集",
                tmdbEpisodeOverview: null);

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("搜索缺失回填单集", result.Item!.Name);
            Assert.IsNotNull(harness.TitleBackfillStore.Peek(harness.Episode.Id), "FullRefresh + replaceAllMetadata=false 应继续排队标题回填 candidate。 ");
            Assert.IsNotNull(harness.OverviewCleanupStore.Peek(harness.Episode.Id), "FullRefresh + replaceAllMetadata=false 应继续排队简介 cleanup candidate。 ");
            Assert.IsFalse(
                InvokeIsOverwriteMetadataRefreshIfAvailable(harness.HttpContextAccessor.HttpContext, harness.Episode.Id),
                "FullRefresh + replaceAllMetadata=false 不应被共享 overwrite classifier 识别为覆盖刷新。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldRequireOfficialSeriesTmdbId_WhenOnlyLegacySeriesTmdbProviderIdExists()
        {
            using var harness = CreateHarness(
                PluginConfiguration.DefaultScraperModeDefault,
                replaceAllMetadata: true,
                metadataLanguage: "zh-CN",
                tmdbEpisodeTitle: "legacy TMDb 单集标题",
                tmdbEpisodeOverview: "父级只有 MetaSharkID=Tmdb_* 时不应进入 TMDb 单集详情。");
            harness.Info.SeriesProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_65942",
                ["MetaSharkTmdbID"] = "65942",
            };

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.QueriedById, "父级 Series 只有 legacy TMDb id 时必须等待 Series 先写回官方 SeriesProviderIds[TMDb]，不能兼容解析旧 key 或 MetaSharkTmdbID。 ");
            Assert.IsNotNull(result.Item);
            Assert.AreEqual("第 1 集", result.Item!.Name);
            Assert.IsNull(result.Item.Overview);
            Assert.IsNull(result.Item.PremiereDate);
            Assert.IsNull(result.Item.CommunityRating);
        }

        private Harness CreateHarness(
            string defaultScraperMode,
            bool replaceAllMetadata,
            string metadataLanguage,
            string tmdbEpisodeTitle,
            string? tmdbEpisodeOverview,
            DoubanApi? doubanApi = null)
        {
            EnsurePluginInstance();
            var configuration = MetaSharkPlugin.Instance!.Configuration;
            configuration.DefaultScraperMode = defaultScraperMode;
            configuration.EnableTmdb = true;
            configuration.EnableTmdbMatch = true;
            configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            return new Harness(
                this.loggerFactory,
                replaceAllMetadata,
                metadataLanguage,
                tmdbEpisodeTitle,
                tmdbEpisodeOverview,
                doubanApi ?? new DoubanApi(this.loggerFactory));
        }

        private static EpisodeProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IEpisodeTitleBackfillCandidateStore titleBackfillStore,
            IEpisodeOverviewCleanupCandidateStore overviewCleanupStore,
            ILoggerFactory loggerFactory,
            DoubanApi doubanApi)
        {
            return new EpisodeProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager,
                httpContextAccessor,
                doubanApi,
                tmdbApi,
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new TvdbApi(loggerFactory),
                titleBackfillStore,
                overviewCleanupStore);
        }

        private static DefaultHttpContext CreateRefreshRequestContext(Guid itemId, bool replaceAllMetadata)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/Items/{itemId:N}/Refresh";
            context.Request.QueryString = new QueryString($"?metadataRefreshMode=FullRefresh&replaceAllMetadata={replaceAllMetadata.ToString().ToLowerInvariant()}");
            return context;
        }

        private static EpisodeInfo CreateEpisodeInfo(string metadataLanguage)
        {
            return new EpisodeInfo
            {
                Name = "第 1 集",
                Path = "/library/tv/overwrite-series/Season 01/episode-01.mkv",
                MetadataLanguage = metadataLanguage,
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
                    : new Jellyfin.Plugin.MetaShark.Model.EpisodeLocalizedValue
                    {
                        Value = overview,
                        SourceLanguage = language,
                    });
        }

        private static bool InvokeIsOverwriteMetadataRefresh(HttpContext? context, Guid? expectedItemId)
        {
            var classifierType = typeof(PluginConfiguration).Assembly.GetType(OverwriteClassifierTypeName);
            Assert.IsNotNull(classifierType, "OverwriteMetadataRefreshClassifier 未定义，Episode 覆盖刷新无法复用共享识别器。 ");

            var method = classifierType!.GetMethod(
                "IsOverwriteMetadataRefresh",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(HttpContext), typeof(Guid?) },
                modifiers: null);
            Assert.IsNotNull(method, "OverwriteMetadataRefreshClassifier.IsOverwriteMetadataRefresh(HttpContext?, Guid?) 未定义。 ");

            var result = method!.Invoke(null, new object?[] { context, expectedItemId });
            Assert.IsNotNull(result, "OverwriteMetadataRefreshClassifier.IsOverwriteMetadataRefresh 应返回布尔值。 ");
            Assert.IsInstanceOfType(result, typeof(bool));
            return (bool)result;
        }

        private static bool InvokeIsOverwriteMetadataRefreshIfAvailable(HttpContext? context, Guid? expectedItemId)
        {
            var classifierType = typeof(PluginConfiguration).Assembly.GetType(OverwriteClassifierTypeName);
            if (classifierType == null)
            {
                return false;
            }

            var method = classifierType.GetMethod(
                "IsOverwriteMetadataRefresh",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(HttpContext), typeof(Guid?) },
                modifiers: null);
            Assert.IsNotNull(method, "OverwriteMetadataRefreshClassifier.IsOverwriteMetadataRefresh(HttpContext?, Guid?) 未定义。 ");

            var result = method!.Invoke(null, new object?[] { context, expectedItemId });
            Assert.IsNotNull(result, "OverwriteMetadataRefreshClassifier.IsOverwriteMetadataRefresh 应返回布尔值。 ");
            Assert.IsInstanceOfType(result, typeof(bool));
            return (bool)result;
        }

        private static DoubanApi CreateThrowingDoubanApi(ILoggerFactory loggerFactory, string message)
        {
            var api = new DoubanApi(loggerFactory);
            var httpClientField = typeof(DoubanApi).GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(httpClientField, "DoubanApi.httpClient 未定义");

            var originalClient = (HttpClient)httpClientField!.GetValue(api)!;
            httpClientField.SetValue(api, new HttpClient(new ThrowingHttpMessageHandler(message), disposeHandler: true));
            originalClient.Dispose();

            return api;
        }

        private static void EnsurePluginInstance()
        {
            if (MetaSharkPlugin.Instance != null)
            {
                EnsurePluginConfiguration();
                return;
            }

            Directory.CreateDirectory(PluginsPath);
            Directory.CreateDirectory(PluginConfigurationsPath);

            var appHost = new Mock<IServerApplicationHost>();
            appHost.Setup(x => x.GetLocalApiUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>())).Returns("http://127.0.0.1:8096");
            var applicationPaths = new Mock<IApplicationPaths>();
            applicationPaths.SetupGet(x => x.PluginsPath).Returns(PluginsPath);
            applicationPaths.SetupGet(x => x.PluginConfigurationsPath).Returns(PluginConfigurationsPath);
            var xmlSerializer = new Mock<IXmlSerializer>();

            _ = new MetaSharkPlugin(appHost.Object, applicationPaths.Object, xmlSerializer.Object);
            EnsurePluginConfiguration();
        }

        private static void EnsurePluginConfiguration()
        {
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);

            if (plugin!.Configuration != null)
            {
                return;
            }

            var configuration = new PluginConfiguration();
            var currentType = plugin.GetType();
            while (currentType != null)
            {
                var configurationProperty = currentType.GetProperty("Configuration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (configurationProperty != null
                    && configurationProperty.PropertyType.IsAssignableFrom(typeof(PluginConfiguration))
                    && configurationProperty.SetMethod != null)
                {
                    configurationProperty.SetValue(plugin, configuration);
                    return;
                }

                var configurationField = currentType
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(field => field.FieldType.IsAssignableFrom(typeof(PluginConfiguration)));
                if (configurationField != null)
                {
                    configurationField.SetValue(plugin, configuration);
                    return;
                }

                currentType = currentType.BaseType;
            }

            Assert.Fail("Could not initialize MetaSharkPlugin configuration for tests.");
        }

        private sealed class Harness : IDisposable
        {
            public Harness(
                ILoggerFactory loggerFactory,
                bool replaceAllMetadata,
                string metadataLanguage,
                string tmdbEpisodeTitle,
                string? tmdbEpisodeOverview,
                DoubanApi doubanApi)
            {
                this.Info = CreateEpisodeInfo(metadataLanguage);
                this.Episode = new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = this.Info.Name,
                    Path = this.Info.Path,
                    Overview = null,
                };
                this.TitleBackfillStore = new InMemoryEpisodeTitleBackfillCandidateStore();
                this.OverviewCleanupStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
                this.HttpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = CreateRefreshRequestContext(this.Episode.Id, replaceAllMetadata),
                };
                this.LibraryManagerStub = new Mock<ILibraryManager>();
                this.LibraryManagerStub
                    .Setup(x => x.FindByPath(this.Info.Path, false))
                    .Returns(this.Episode);

                var tmdbApi = new TmdbApi(loggerFactory);
                SeedEpisode(tmdbApi, 123, 1, 1, metadataLanguage, metadataLanguage, new TvEpisode
                {
                    Name = tmdbEpisodeTitle,
                    Overview = tmdbEpisodeOverview,
                    AirDate = new DateTime(2024, 6, 8),
                    VoteAverage = 8.7,
                });
                SeedEpisodeTranslationOverview(tmdbApi, 123, 1, 1, metadataLanguage, null);
                SeedEpisode(tmdbApi, 65942, 1, 1, metadataLanguage, metadataLanguage, new TvEpisode
                {
                    Name = "不应读取 legacy TMDb 单集标题",
                    Overview = "如果解析 MetaSharkID=Tmdb_65942 或 MetaSharkTmdbID=65942，就会得到这段错误简介。",
                    AirDate = new DateTime(2024, 1, 1),
                    VoteAverage = 1.1,
                });
                SeedEpisodeTranslationOverview(tmdbApi, 65942, 1, 1, metadataLanguage, null);

                this.Provider = CreateProvider(
                    this.LibraryManagerStub.Object,
                    this.HttpContextAccessor,
                    tmdbApi,
                    this.TitleBackfillStore,
                    this.OverviewCleanupStore,
                    loggerFactory,
                    doubanApi);
            }

            public EpisodeInfo Info { get; }

            public Episode Episode { get; }

            public InMemoryEpisodeTitleBackfillCandidateStore TitleBackfillStore { get; }

            public InMemoryEpisodeOverviewCleanupCandidateStore OverviewCleanupStore { get; }

            public IHttpContextAccessor HttpContextAccessor { get; }

            public Mock<ILibraryManager> LibraryManagerStub { get; }

            public EpisodeProvider Provider { get; }

            public void Dispose()
            {
                this.Provider.Dispose();
            }
        }

        private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
        {
            private readonly string message;

            public ThrowingHttpMessageHandler(string message)
            {
                this.message = message;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException(this.message + " Request: " + request.RequestUri);
            }
        }
    }
}
