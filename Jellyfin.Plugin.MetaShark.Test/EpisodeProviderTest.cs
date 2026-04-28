using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeProviderTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-provider-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.SingleLine = true;
                    options.TimestampFormat = "hh:mm:ss ";
                }));

        [TestInitialize]
        public void ResetConfigurationBeforeTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void ResetConfigurationAfterTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
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

        private static void ReplacePluginConfiguration(PluginConfiguration configuration)
        {
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);

            var currentType = plugin!.GetType();
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

            Assert.Fail("Could not replace MetaSharkPlugin configuration for tests.");
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



        [TestMethod]
        public void TestGetMetadata()
        {
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);
            var tvdbApi = new TvdbApi(loggerFactory);

            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();

            Task.Run(async () =>
            {
                var info = new EpisodeInfo()
                {
                    Name = "Spice and Wolf",
                    Path = "/test/Spice and Wolf/S00/[VCB-Studio] Spice and Wolf II [01][Hi444pp_1080p][x264_flac].mkv",
                    MetadataLanguage = "zh",
                    ParentIndexNumber = 0,
                    SeriesProviderIds = new Dictionary<string, string>() { { BaseProvider.MetaSharkTmdbProviderId, "26707" } },
                    IsAutomated = false,
                };
                var provider = new EpisodeProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi, tvdbApi);
                var result = await provider.GetMetadata(info, CancellationToken.None);
                Assert.IsNotNull(result);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestFixParseInfo()
        {
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);
            var tvdbApi = new TvdbApi(loggerFactory);

            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();


            var provider = new EpisodeProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi, tvdbApi);
            var parseResult = provider.FixParseInfo(new EpisodeInfo() { Path = "/test/[POPGO][Stand_Alone_Complex][05][1080P][BluRay][x264_FLACx2_AC3x1][chs_jpn][D87C36B6].mkv" });
            Assert.AreEqual(parseResult.IndexNumber, 5);

            parseResult = provider.FixParseInfo(new EpisodeInfo() { Path = "/test/Fullmetal Alchemist Brotherhood.E05.1920X1080" });
            Assert.AreEqual(parseResult.IndexNumber, 5);

            parseResult = provider.FixParseInfo(new EpisodeInfo() { Path = "/test/[SAIO-Raws] Neon Genesis Evangelion 05 [BD 1440x1080 HEVC-10bit OPUSx2 ASSx2].mkv" });
            Assert.AreEqual(parseResult.IndexNumber, 5);

            parseResult = provider.FixParseInfo(new EpisodeInfo() { Path = "/test/[Moozzi2] Samurai Champloo [SP03] Battlecry (Opening) PV (BD 1920x1080 x.264 AC3).mkv" });
            Assert.AreEqual(parseResult.IndexNumber, 3);
            Assert.AreEqual(parseResult.ParentIndexNumber, 0);
        }

        [TestMethod]
        public void TmdbOnlyModeDoesNotChangeEpisodeMetadataBehavior()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;
            var originalEnableTmdbMatch = plugin.Configuration.EnableTmdbMatch;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;
                plugin.Configuration.EnableTmdbMatch = false;

                var tmdbApi = new TmdbApi(loggerFactory);
                SeedEpisode(tmdbApi, 26707, 1, 1, "zh-CN", "zh-CN", new TvEpisode
                {
                    Name = "狼与香辛料",
                    Overview = "旅行商人与贤狼重逢的故事。",
                    AirDate = new DateTime(2008, 1, 9),
                    VoteAverage = 8.4,
                });

                var doubanApi = new DoubanApi(loggerFactory);
                var omdbApi = new OmdbApi(loggerFactory);
                var imdbApi = new ImdbApi(loggerFactory);
                var tvdbApi = new TvdbApi(loggerFactory);
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessorStub = new Mock<IHttpContextAccessor>();

                Task.Run(async () =>
                {
                    var info = new EpisodeInfo()
                    {
                        Name = "第 1 集",
                        Path = "/test/Spice and Wolf/S01/episode-01.mkv",
                        MetadataLanguage = "zh-CN",
                        ParentIndexNumber = 1,
                        IndexNumber = 1,
                        SeriesProviderIds = new Dictionary<string, string>() { { BaseProvider.MetaSharkTmdbProviderId, "26707" } },
                        IsAutomated = true,
                    };

                    var provider = new EpisodeProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi, tvdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsNotNull(result.Item, "EpisodeProvider 不应因 tmdb-only 配置而失去既有 TMDb 剧集元数据路径。 ");
                    Assert.IsTrue(result.HasMetadata);
                    Assert.AreEqual("狼与香辛料", result.Item.Name);
                    Assert.AreEqual(1, result.Item.IndexNumber);
                    Assert.AreEqual(1, result.Item.ParentIndexNumber);
                    Assert.AreEqual(new DateTime(2008, 1, 9), result.Item.PremiereDate);
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
                plugin.Configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_DoesNotAddEpisodePeople()
        {
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 26707, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "狼与香辛料",
                Overview = "旅行商人与贤狼重逢的故事。",
                AirDate = new DateTime(2008, 1, 9),
                VoteAverage = 8.4,
            });

            var doubanApi = new DoubanApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);
            var tvdbApi = new TvdbApi(loggerFactory);
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();

            var info = new EpisodeInfo()
            {
                Name = "第 1 集",
                Path = "/test/Spice and Wolf/S01/episode-01.mkv",
                MetadataLanguage = "zh-CN",
                ParentIndexNumber = 1,
                IndexNumber = 1,
                SeriesProviderIds = new Dictionary<string, string>() { { BaseProvider.MetaSharkTmdbProviderId, "26707" } },
                IsAutomated = true,
            };

            var provider = new EpisodeProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi, tvdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("狼与香辛料", result.Item.Name);
            Assert.AreEqual(1, result.Item.IndexNumber);
            Assert.AreEqual(1, result.Item.ParentIndexNumber);
            Assert.AreEqual(new DateTime(2008, 1, 9), result.Item.PremiereDate);
            Assert.IsTrue(result.People == null || result.People.Count == 0, "EpisodeProvider 不应向单集元数据写入任何演职人员。 ");
        }

        [TestMethod]
        public async Task EpisodeProviderLog_GetSearchResults_UsesChineseSummary()
        {
            var providerLogger = new Mock<ILogger>();
            providerLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var providerLoggerFactory = new Mock<ILoggerFactory>();
            providerLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(providerLogger.Object);

            using var apiLoggerFactory = LoggerFactory.Create(builder => { });
            var provider = new EpisodeProvider(
                new DefaultHttpClientFactory(),
                providerLoggerFactory.Object,
                new Mock<ILibraryManager>().Object,
                new Mock<IHttpContextAccessor>().Object,
                new DoubanApi(apiLoggerFactory),
                new TmdbApi(apiLoggerFactory),
                new OmdbApi(apiLoggerFactory),
                new ImdbApi(apiLoggerFactory),
                new TvdbApi(apiLoggerFactory));

            var results = (await provider.GetSearchResults(new EpisodeInfo { Name = string.Empty }, CancellationToken.None).ConfigureAwait(false)).ToList();

            Assert.AreEqual(0, results.Count);
            LogAssert.AssertLoggedOnce(
                providerLogger,
                LogLevel.Information,
                expectException: false,
                originalFormatContains: "[MetaShark] {Message}",
                messageContains: ["[MetaShark] 开始搜索剧集单集候选. name: "]);
        }

        [TestMethod]
        public async Task EpisodeProviderLog_GetMetadata_WhenTvdbIdMissing_UsesChineseSkipMessage()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableTvdbSpecialsWithinSeasons = true;

            var providerLogger = new Mock<ILogger>();
            providerLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var providerLoggerFactory = new Mock<ILoggerFactory>();
            providerLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(providerLogger.Object);

            using var apiLoggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(apiLoggerFactory);
            SeedEpisode(tmdbApi, 0, 0, 1, "en", "en", new TvEpisode
            {
                Name = "Pilot",
                Overview = "Seeded overview",
                AirDate = new DateTime(2024, 1, 1),
                VoteAverage = 8.1,
            });

            var provider = new EpisodeProvider(
                new DefaultHttpClientFactory(),
                providerLoggerFactory.Object,
                new Mock<ILibraryManager>().Object,
                new Mock<IHttpContextAccessor>().Object,
                new DoubanApi(apiLoggerFactory),
                tmdbApi,
                new OmdbApi(apiLoggerFactory),
                new ImdbApi(apiLoggerFactory),
                new TvdbApi(apiLoggerFactory));

            var result = await provider.GetMetadata(
                new EpisodeInfo
                {
                    Name = "Episode 1",
                    Path = "/test/Series/S00/episode-01.mkv",
                    MetadataLanguage = "en",
                    ParentIndexNumber = 0,
                    IndexNumber = 1,
                    SeriesProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), "not-an-int" },
                    },
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            LogAssert.AssertLoggedAtLeastOnce(
                providerLogger,
                LogLevel.Information,
                expectException: false,
                originalFormatContains: "[MetaShark] {Message}",
                messageContains: ["[MetaShark] 开始获取单集元数据. name: Episode 1"]);
            LogAssert.AssertLoggedOnce(
                providerLogger,
                LogLevel.Information,
                expectException: false,
                originalFormatContains: "[MetaShark] {Message}",
                messageContains: ["[MetaShark] 跳过 TVDB 特别篇定位，缺少 TVDB id. s0e1"]);
        }

        [TestMethod]
        public async Task EpisodeProviderLog_GetMetadata_WhenTvdbIdPresent_UsesChineseStructuredMessages()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableTvdbSpecialsWithinSeasons = true;

            var providerLogger = new Mock<ILogger>();
            providerLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var providerLoggerFactory = new Mock<ILoggerFactory>();
            providerLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(providerLogger.Object);

            using var apiLoggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(apiLoggerFactory);
            SeedEpisode(tmdbApi, 0, 0, 1, "en", "en", new TvEpisode
            {
                Name = "Pilot",
                Overview = "Seeded overview",
                AirDate = new DateTime(2024, 1, 1),
                VoteAverage = 8.1,
            });

            var provider = new EpisodeProvider(
                new DefaultHttpClientFactory(),
                providerLoggerFactory.Object,
                new Mock<ILibraryManager>().Object,
                new Mock<IHttpContextAccessor>().Object,
                new DoubanApi(apiLoggerFactory),
                tmdbApi,
                new OmdbApi(apiLoggerFactory),
                new ImdbApi(apiLoggerFactory),
                new TvdbApi(apiLoggerFactory));

            var result = await provider.GetMetadata(
                new EpisodeInfo
                {
                    Name = "Episode 1",
                    Path = "/test/Series/S00/episode-01.mkv",
                    MetadataLanguage = "en",
                    ParentIndexNumber = 0,
                    IndexNumber = 1,
                    SeriesProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), "not-an-int" },
                        { MetadataProvider.Tvdb.ToString(), "321" },
                    },
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            LogAssert.AssertLoggedOnce(
                providerLogger,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    { "TvdbId", "321" },
                    { "Season", 0 },
                    { "Episode", 1 },
                    { "Lang", "en" },
                },
                originalFormatContains: "[MetaShark] 查询 TVDB 特别篇定位");
            LogAssert.AssertLoggedOnce(
                providerLogger,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    { "TvdbId", "321" },
                    { "Season", 0 },
                    { "Episode", 1 },
                },
                originalFormatContains: "[MetaShark] 未找到 TVDB 特别篇定位");
        }

    }
}
