using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
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
using System.Reflection;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class EpisodeProviderScheduledSearchMissingMetadataCompatibilityTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-provider-scheduled-search-compatibility-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestCleanup]
        public void Cleanup()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = false;
        }

        [TestMethod]
        public async Task ScheduledSearchWithoutRefreshQuery_WhenOriginalTitleStillDefault_QueuesTitleBackfillCandidate()
        {
            using var harness = CreateHarness(
                currentEpisodeTitle: "第 1 集",
                currentEpisodeOverview: null,
                tmdbEpisodeName: "皇后回宫",
                tmdbEpisodeOverview: null,
                tmdbTranslationOverview: "雫第一次参加神之水滴选拔挑战。");

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var titleCandidate = harness.TitleCandidateStore.Peek(harness.Episode.Id);
            var overviewCandidate = harness.OverviewCleanupCandidateStore.Peek(harness.Episode.Id);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(titleCandidate, "计划任务路径没有显式 refresh query 时，只要原始标题仍是默认“第 N 集”且当前标题未被人工改动，provider 仍应沿用隐式兼容语义生成 title backfill candidate。");
            Assert.AreEqual(harness.Episode.Id, titleCandidate!.ItemId);
            Assert.AreEqual(harness.Info.Path, titleCandidate.ItemPath);
            Assert.AreEqual("第 1 集", titleCandidate.OriginalTitleSnapshot);
            Assert.AreEqual("皇后回宫", titleCandidate.CandidateTitle);
            Assert.IsNull(overviewCandidate, "这个用例已提供可信 episode overview，避免把标题兼容回归和 overview cleanup 语义混在一起。 ");
        }

        [TestMethod]
        public async Task ScheduledSearchWithoutRefreshQuery_WhenOriginalOverviewIsEmpty_QueuesOverviewCleanupCandidate()
        {
            using var harness = CreateHarness(
                currentEpisodeTitle: "勇者的肋骨",
                currentEpisodeOverview: null,
                tmdbEpisodeName: "勇者的肋骨",
                tmdbEpisodeOverview: null,
                tmdbTranslationOverview: "   ");

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var titleCandidate = harness.TitleCandidateStore.Peek(harness.Episode.Id);
            var overviewCandidate = harness.OverviewCleanupCandidateStore.Peek(harness.Episode.Id);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("勇者的肋骨", result.Item!.Name);
            Assert.AreEqual(null, result.Item.Overview, "provider 未拿到可信 episode overview 时，本轮返回仍应为空。 ");
            Assert.IsNull(titleCandidate, "当前标题已经不是默认占位值时，overview cleanup 兼容路径不应误触发 title backfill candidate。 ");
            Assert.IsNotNull(overviewCandidate, "计划任务路径没有显式 refresh query 时，只要 provider-time original overview 为空，就应继续生成 overview cleanup candidate。 ");
            Assert.AreEqual(harness.Episode.Id, overviewCandidate!.ItemId);
            Assert.AreEqual(harness.Info.Path, overviewCandidate.ItemPath);
            Assert.AreEqual(string.Empty, overviewCandidate.OriginalOverviewSnapshot);
        }

        private CompatibilityHarness CreateHarness(
            string currentEpisodeTitle,
            string? currentEpisodeOverview,
            string tmdbEpisodeName,
            string? tmdbEpisodeOverview,
            string? tmdbTranslationOverview)
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;
            return new CompatibilityHarness(this.loggerFactory, currentEpisodeTitle, currentEpisodeOverview, tmdbEpisodeName, tmdbEpisodeOverview, tmdbTranslationOverview);
        }

        private static EpisodeProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IEpisodeTitleBackfillCandidateStore titleBackfillCandidateStore,
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
                titleBackfillCandidateStore,
                overviewCleanupCandidateStore);
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

        private sealed class CompatibilityHarness : IDisposable
        {
            public CompatibilityHarness(
                ILoggerFactory loggerFactory,
                string currentEpisodeTitle,
                string? currentEpisodeOverview,
                string tmdbEpisodeName,
                string? tmdbEpisodeOverview,
                string? tmdbTranslationOverview)
            {
                this.TitleCandidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
                this.OverviewCleanupCandidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
                this.Info = CreateEpisodeInfo();
                this.Episode = new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = currentEpisodeTitle,
                    Path = this.Info.Path,
                    Overview = currentEpisodeOverview,
                };

                this.LibraryManagerStub = new Mock<ILibraryManager>();
                this.LibraryManagerStub
                    .Setup(x => x.FindByPath(this.Info.Path, false))
                    .Returns(this.Episode);

                this.HttpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = null,
                };

                var tmdbApi = new TmdbApi(loggerFactory);
                SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
                {
                    Name = tmdbEpisodeName,
                    Overview = tmdbEpisodeOverview,
                });
                SeedEpisodeTranslationOverview(tmdbApi, 123, 1, 1, "zh-CN", tmdbTranslationOverview);

                this.Provider = CreateProvider(
                    this.LibraryManagerStub.Object,
                    this.HttpContextAccessor,
                    tmdbApi,
                    this.TitleCandidateStore,
                    this.OverviewCleanupCandidateStore,
                    loggerFactory);
            }

            public InMemoryEpisodeTitleBackfillCandidateStore TitleCandidateStore { get; }

            public InMemoryEpisodeOverviewCleanupCandidateStore OverviewCleanupCandidateStore { get; }

            public EpisodeInfo Info { get; }

            public Episode Episode { get; }

            public IHttpContextAccessor HttpContextAccessor { get; }

            public Mock<ILibraryManager> LibraryManagerStub { get; }

            public EpisodeProvider Provider { get; }

            public void Dispose()
            {
                this.Provider.Dispose();
            }
        }
    }
}
