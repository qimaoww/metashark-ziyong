using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class SeasonProviderTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-season-provider-tests");
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

        private static IMemoryCache GetTmdbMemoryCache(TmdbApi tmdbApi)
        {
            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "TmdbApi.memoryCache 未定义");

            var memoryCache = memoryCacheField!.GetValue(tmdbApi) as IMemoryCache;
            Assert.IsNotNull(memoryCache, "TmdbApi.memoryCache 不是有效的 IMemoryCache");
            return memoryCache!;
        }

        private static IMemoryCache GetDoubanMemoryCache(DoubanApi doubanApi)
        {
            var memoryCacheField = typeof(DoubanApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "DoubanApi.memoryCache 未定义");

            var memoryCache = memoryCacheField!.GetValue(doubanApi) as IMemoryCache;
            Assert.IsNotNull(memoryCache, "DoubanApi.memoryCache 不是有效的 IMemoryCache");
            return memoryCache!;
        }

        private static void SeedDoubanSubject(DoubanApi doubanApi, DoubanSubject subject)
        {
            var cache = GetDoubanMemoryCache(doubanApi);
            cache.Set($"movie_{subject.Sid}", subject, TimeSpan.FromMinutes(5));
            cache.Set($"celebrities_{subject.Sid}", new List<DoubanCelebrity>(), TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbSeason(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, string language, string seasonName)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"season-{seriesTmdbId}-s{seasonNumber}-{language}-{language}",
                new TvSeason
                {
                    Name = seasonName,
                    Overview = "TMDb seeded season overview",
                    AirDate = new DateTime(2015, 2, 1),
                },
                TimeSpan.FromMinutes(5));
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

        [TestMethod]
        public void TestGetMetadata()
        {
            var info = new SeasonInfo() { Name = "第 18 季", IndexNumber = 18, SeriesProviderIds = new Dictionary<string, string>() { { BaseProvider.DoubanProviderId, "2059529" }, { MetadataProvider.Tmdb.ToString(), "34860" } } };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                var provider = new SeasonProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetMetadata(info, CancellationToken.None);
                Assert.IsNotNull(result);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public async Task GetMetadataByDouban_DoesNotAddSeasonPeople()
        {
            var info = new SeasonInfo
            {
                Name = "第1季",
                IndexNumber = 1,
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "season-douban-100" },
                },
                SeriesProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "series-douban-100" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            SeedDoubanSubject(
                doubanApi,
                new DoubanSubject
                {
                    Sid = "season-douban-100",
                    Name = "豆瓣示例剧集 第1季",
                    Year = 2024,
                    Rating = 8.1f,
                    Genre = "剧情 / 动画",
                    Intro = "豆瓣季简介",
                    Screen = "2024-04-04",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000010.webp",
                });
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            var provider = new SeasonProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("豆瓣示例剧集 第1季", result.Item.Name);
            Assert.AreEqual("豆瓣季简介", result.Item.Overview);
            Assert.AreEqual(1, result.Item.IndexNumber);
            Assert.AreEqual("season-douban-100", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.IsTrue(result.People == null || result.People.Count == 0, "季级 Douban 元数据不应再写入任何人物。 ");
        }

        [TestMethod]
        public void TestGuessSeasonNumberByFileName()
        {
            var info = new SeasonInfo() { };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            var provider = new SeasonProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);

            var result = provider.GuessSeasonNumberByDirectoryName("/data/downloads/jellyfin/tv/冰与火之歌S01-S08.Game.of.Thrones.1080p.Blu-ray.x265.10bit.AC3/冰与火之歌S2.列王的纷争.2012.1080p.Blu-ray.x265.10bit.AC3");
            Assert.AreEqual(result, 2);

            result = provider.GuessSeasonNumberByDirectoryName("/data/downloads/jellyfin/tv/向往的生活/第2季");
            Assert.AreEqual(result, 2);

            result = provider.GuessSeasonNumberByDirectoryName("/data/downloads/jellyfin/tv/向往的生活 第2季");
            Assert.AreEqual(result, 2);

            result = provider.GuessSeasonNumberByDirectoryName("/data/downloads/jellyfin/tv/向往的生活/第三季");
            Assert.AreEqual(result, 3);

            result = provider.GuessSeasonNumberByDirectoryName("/data/downloads/jellyfin/tv/攻壳机动队Ghost_in_The_Shell_S.A.C._2nd_GIG");
            Assert.AreEqual(result, 2);

            // result = provider.GuessSeasonNumberByDirectoryName("/data/downloads/jellyfin/tv/Spice and Wolf/Spice and Wolf 2");
            // Assert.AreEqual(result, 2);

            result = provider.GuessSeasonNumberByDirectoryName("/data/downloads/jellyfin/tv/Spice and Wolf/Spice and Wolf 2 test");
            Assert.AreEqual(result, null);

            result = provider.GuessSeasonNumberByDirectoryName("/data/downloads/jellyfin/tv/[BDrip] Made in Abyss S02 [7鲁ACG x Sakurato]");
            Assert.AreEqual(result, 2);
        }

        [TestMethod]
        public void TestGuestDoubanSeasonByYearAsync()
        {
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                var provider = new SeasonProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GuestDoubanSeasonByYearAsync("机动战士高达0083 星尘的回忆", 1991, null, CancellationToken.None);

                if (!string.IsNullOrEmpty(result))
                {
                    Assert.AreEqual("1766564", result);
                }
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void DefaultScraperPolicy_TmdbOnlyReroutesToTmdbWhenSeriesTmdbIdAvailable()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;
            var originalEnableTmdb = plugin.Configuration.EnableTmdb;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;
                plugin.Configuration.EnableTmdb = true;
                Assert.AreEqual(PluginConfiguration.DefaultScraperModeTmdbOnly, plugin.Configuration.DefaultScraperMode);
                Assert.IsFalse(Jellyfin.Plugin.MetaShark.Providers.DefaultScraperPolicy.IsDoubanAllowed(plugin.Configuration, DefaultScraperSemantic.AutomaticRefresh));

                var info = new SeasonInfo()
                {
                    Name = "当前中文标题",
                    IndexNumber = 18,
                    MetadataLanguage = "zh",
                    IsAutomated = true,
                    SeriesProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "2059529" },
                        { MetadataProvider.Tmdb.ToString(), "34860" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = null,
                };
                var doubanApi = CreateThrowingDoubanApi(loggerFactory, "tmdb-only 自动季元数据链路不应再访问 Douban。");
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbSeason(tmdbApi, 34860, 18, "zh", "Season 18");
                var omdbApi = new OmdbApi(loggerFactory);
                var imdbApi = new ImdbApi(loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeasonProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsNotNull(result.Item, "tmdb-only 应保留有效的 seriesTmdbId -> GetMetadataByTmdb(...) 季级回退。");
                    Assert.IsTrue(result.HasMetadata);
                    Assert.AreEqual("Season 18", result.Item.Name, "普通 TMDb season 路径应继续直接采用 GetSeasonAsync(...) 的英文季名，不受显式剧集组英文保护影响。 ");
                    Assert.AreEqual(18, result.Item.IndexNumber);
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
        }
    }
}
