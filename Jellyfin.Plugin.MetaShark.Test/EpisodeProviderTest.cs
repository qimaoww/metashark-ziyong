using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping;
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
using System.Net;
using System.Net.Http;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using TMDbLib.Objects.General;
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



        [TestMethod]
        public async Task GetMetadata_WhenEpisodeGroupMapsToTmdbAbsoluteEpisode_KeepsEpisodeGroupNumber()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbEpisodeGroupMap = "65942=rezero-production-group",
            });

            var tmdbApi = new TmdbApi(loggerFactory);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(
                tmdbApi,
                "rezero-production-group",
                "zh-CN",
                ExplicitEpisodeGroupMappingTestHelper.CreateGroup(
                    order: 4,
                    name: "Season 4",
                    ExplicitEpisodeGroupMappingTestHelper.CreateEpisode(order: 9, seasonNumber: 1, episodeNumber: 76)));
            SeedEpisode(tmdbApi, 65942, 1, 76, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "杀人会成为一种习惯",
                Overview = "昴一行人开始在唯有踏沙声回响的地下通道中前进。",
                AirDate = new DateTime(2025, 3, 5),
                VoteAverage = 8.4,
            });
            SeedEpisodeTranslationOverview(tmdbApi, 65942, 1, 76, "zh-CN", null);

            var provider = CreateProvider(new Mock<ILibraryManager>().Object, new Mock<IHttpContextAccessor>().Object, tmdbApi);
            var result = await provider.GetMetadata(
                new EpisodeInfo
                {
                    Name = "第10集",
                    Path = "/test/Re：从零开始的异世界生活 (2016)/Season 4/Re：从零开始的异世界生活 - S04E10 - 第10集.mkv",
                    MetadataLanguage = "zh-CN",
                    ParentIndexNumber = 4,
                    IndexNumber = 10,
                    SeriesDisplayOrder = "production",
                    SeriesProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), "65942" },
                    },
                    IsAutomated = true,
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual(4, result.Item!.ParentIndexNumber, "Jellyfin 应保留剧集组内的季号，而不是 TMDb 原始季号。");
            Assert.AreEqual(10, result.Item.IndexNumber, "Jellyfin 应保留剧集组内的集号，而不是 TMDb 原始集号。");
            Assert.AreEqual("杀人会成为一种习惯", result.Item.Name);
            Assert.AreEqual("昴一行人开始在唯有踏沙声回响的地下通道中前进。", result.Item.Overview);
            Assert.AreEqual(new DateTime(2025, 3, 5), result.Item.PremiereDate);
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
                    SeriesProviderIds = new Dictionary<string, string>() { { MetadataProvider.Tmdb.ToString(), "26707" } },
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
        public void FixParseInfo_CharacterizesCurrentInPlaceLookupMutation()
        {
            var provider = CreateProvider(new Mock<ILibraryManager>().Object, new Mock<IHttpContextAccessor>().Object, new TmdbApi(loggerFactory));
            var info = new EpisodeInfo
            {
                Name = "Manually Curated Title",
                Path = "/test/Detective Dee/Season 02/Detective.Dee.S02EP03.2006.2160p.WEB-DL.x264.AAC-HQC.mkv",
                MetadataLanguage = "zh-CN",
                ParentIndexNumber = 1,
                IndexNumber = 1,
                Year = 2024,
                SeriesProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "26707",
                },
            };

            var parseResult = provider.FixParseInfo(info);

            Assert.AreSame(info, parseResult, "FixParseInfo 当前会原地修改调用方传入的 EpisodeInfo，而不是返回不可变副本。");
            Assert.AreEqual(2, info.ParentIndexNumber, "文件名中的 S02 会覆盖传入季号。");
            Assert.AreEqual(3, info.IndexNumber, "文件名中的 EP03 会覆盖传入集号。");
            Assert.AreEqual(2006, info.Year, "文件名中的年份会覆盖传入年份。");
            Assert.AreEqual("Detective Dee", info.Name, "当前解析副作用会覆盖传入 lookup 名称。");
            Assert.AreEqual("zh-CN", info.MetadataLanguage);
            Assert.AreEqual("26707", info.SeriesProviderIds[MetadataProvider.Tmdb.ToString()]);
        }

        [TestMethod]
        public async Task GetMetadata_CharacterizesParseMutationDrivingEpisodeLookupButOriginalTitlePersistence()
        {
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 26707, 2, 3, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "解析后的第二季第三集",
                Overview = "第二季第三集简介。",
                AirDate = new DateTime(2006, 10, 3),
                VoteAverage = 7.6,
            });
            SeedEpisodeTranslationOverview(tmdbApi, 26707, 2, 3, "zh-CN", null);

            var info = new EpisodeInfo
            {
                Name = "第 1 集",
                Path = "/test/Detective Dee/Season 01/Detective.Dee.S02EP03.2006.2160p.WEB-DL.x264.AAC-HQC.mkv",
                MetadataLanguage = "zh-CN",
                ParentIndexNumber = 1,
                IndexNumber = 1,
                SeriesProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "26707",
                },
                IsAutomated = true,
            };
            var provider = CreateProvider(new Mock<ILibraryManager>().Object, new Mock<IHttpContextAccessor>().Object, tmdbApi);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(2, info.ParentIndexNumber, "GetMetadata 当前会通过 FixParseInfo 原地修正传入 lookup 的季号。");
            Assert.AreEqual(3, info.IndexNumber, "GetMetadata 当前会通过 FixParseInfo 原地修正传入 lookup 的集号。");
            Assert.AreEqual(2006, info.Year, "GetMetadata 当前会通过 FixParseInfo 原地修正传入 lookup 的年份。");
            Assert.AreEqual("Detective Dee", info.Name, "GetMetadata 当前会通过 FixParseInfo 原地修正传入 lookup 的名称。");
            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.IsTrue(result.QueriedById, "被解析后的 s2e3 必须继续驱动 TMDb 单集查询。");
            Assert.AreEqual(2, result.Item!.ParentIndexNumber);
            Assert.AreEqual(3, result.Item.IndexNumber);
            Assert.AreEqual("解析后的第二季第三集", result.Item.Name, "原始标题快照仍用于既有标题持久化策略，不能由解析后的 lookup 名称替代。");
            Assert.AreEqual("第二季第三集简介。", result.Item.Overview);
            Assert.AreEqual(new DateTime(2006, 10, 3), result.Item.PremiereDate);
            Assert.AreEqual(2006, result.Item.ProductionYear);
            Assert.AreEqual(7.6f, result.Item.CommunityRating);
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
                        SeriesProviderIds = new Dictionary<string, string>() { { MetadataProvider.Tmdb.ToString(), "26707" } },
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
        public async Task GetMetadata_WhenTmdbEpisodeUnexpectedHttpError_ReturnsNoMetadataWithoutThrowing()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                LlmAllowTextCompletion = false,
            });

            var tmdbApi = new TmdbApi(loggerFactory);
            ConfigureTmdbClient(tmdbApi, new HttpClient(new StatusHttpMessageHandler(HttpStatusCode.InternalServerError)));

            var provider = new EpisodeProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                new Mock<ILibraryManager>().Object,
                new Mock<IHttpContextAccessor>().Object,
                new DoubanApi(loggerFactory),
                tmdbApi,
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new TvdbApi(loggerFactory));

            var result = await provider.GetMetadata(
                new EpisodeInfo
                {
                    Name = "Episode 3",
                    Path = "/test/Series/S02/episode-03.mkv",
                    MetadataLanguage = "zh-CN",
                    ParentIndexNumber = 2,
                    IndexNumber = 3,
                    SeriesProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), "1" },
                    },
                    IsAutomated = true,
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result, "EpisodeProvider 应返回空元数据结果，而不是让 TMDb HTTP 异常冒泡。 ");
            Assert.IsTrue(result.HasMetadata, "EpisodeProvider 应按既有空结果语义返回可接受结果，而不是让 TMDb 异常冒泡。 ");
            Assert.IsNotNull(result.Item, "EpisodeProvider fail-closed 时应保持既有空 item 结果语义。 ");
            Assert.AreEqual("Episode 3", result.Item!.Name, "TMDb 单集详情不可用时应保留查询标题，不应写入 TMDb 标题。 ");
            Assert.IsNull(result.Item.Overview, "TMDb 单集详情不可用时不应写入简介。 ");
            Assert.IsNull(result.Item.PremiereDate, "TMDb 单集详情不可用时不应写入首播日期。 ");
        }

        private EpisodeProvider CreateProvider(ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, TmdbApi tmdbApi)
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
                new TvdbApi(loggerFactory));
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
                SeriesProviderIds = new Dictionary<string, string>() { { MetadataProvider.Tmdb.ToString(), "26707" } },
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


        private static void ConfigureTmdbClient(TmdbApi api, HttpClient httpClient)
        {
            var tmdbClientField = typeof(TmdbApi).GetField("tmDbClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(tmdbClientField, "TmdbApi.tmDbClient 未定义。 ");
            var tmdbClient = tmdbClientField!.GetValue(api);
            Assert.IsNotNull(tmdbClient, "TmdbApi.tmDbClient 不是有效对象。 ");
            var setConfigMethod = tmdbClient!.GetType().GetMethod("SetConfig", new[] { typeof(TMDbConfig) });
            Assert.IsNotNull(setConfigMethod, "TMDbClient.SetConfig 未定义。 ");
            setConfigMethod!.Invoke(tmdbClient, new object[] { new TMDbConfig() });

            var restClientField = tmdbClient.GetType().GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(restClientField, "TMDbClient._client 未定义。 ");
            var restClient = restClientField!.GetValue(tmdbClient);
            Assert.IsNotNull(restClient, "TMDbClient._client 不是有效对象。 ");
            var httpClientProperty = restClient!.GetType().GetProperty("HttpClient", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(httpClientProperty, "RestClient.HttpClient 未定义。 ");
            httpClientProperty!.SetValue(restClient, httpClient);
        }

        private sealed class StatusHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode statusCode;

            public StatusHttpMessageHandler(HttpStatusCode statusCode)
            {
                this.statusCode = statusCode;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(this.statusCode)
                {
                    Content = new StringContent("server error"),
                });
            }
        }

    }
}
