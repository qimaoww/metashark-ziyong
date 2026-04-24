using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.General;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class SeriesImageProviderTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-series-image-provider-tests");
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

        [TestMethod]
        public void TestGetImages()
        {
            var info = new MediaBrowser.Controller.Entities.TV.Series()
            {
                Name = "花牌情缘",
                PreferredMetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string> { { BaseProvider.DoubanProviderId, "6439459" }, { MetadataProvider.Tmdb.ToString(), "45247" } }
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var provider = new SeriesImageProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetImages(info, CancellationToken.None);
                    Assert.IsNotNull(result);

                    var str = result.ToJson();
                    Console.WriteLine(result.ToJson());
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests
                    || ex.Message.Contains("429", StringComparison.Ordinal))
                {
                    Assert.Inconclusive("Douban rate limited (429)." + ex.Message);
                }
            }).GetAwaiter().GetResult();
        }


        [TestMethod]
        public void TestGetImagesFromTMDB()
        {
            var info = new MediaBrowser.Controller.Entities.TV.Series()
            {
                PreferredMetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), "67534" }, { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() } }
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                var provider = new SeriesImageProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetImages(info, CancellationToken.None);
                Assert.IsNotNull(result);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetImagesFallsBackToTmdbWhenDoubanBlocked()
        {
            var info = new MediaBrowser.Controller.Entities.TV.Series()
            {
                Name = "花牌情缘",
                PreferredMetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "6439459" },
                    { MetadataProvider.Tmdb.ToString(), "45247" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = DoubanApiTestHelper.CreateBlockedDoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var provider = new SeriesImageProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();
                    Assert.IsTrue(images.Any(), "Douban blocked 后应继续回退 TMDb 并返回图片。");
                    Assert.IsTrue(images.Any(image => image.Url?.Contains("tmdb", StringComparison.OrdinalIgnoreCase) == true), "应返回 TMDb 图片 URL。");
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests
                    || ex.Message.Contains("429", StringComparison.Ordinal))
                {
                    Assert.Inconclusive("TMDb rate limited (429)." + ex.Message);
                }
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void DefaultScraperPolicy_TmdbOnlyAutomaticImagesSkipDoubanAndUseTmdbFallback()
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

                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "花牌情缘",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "6439459" },
                        { MetadataProvider.Tmdb.ToString(), "45247" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动剧集图片链路不应再访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeriesDetails(tmdbApi, 45247, "zh");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.IsTrue(images.Any(), "tmdb-only 自动剧集图片链路在存在有效 TMDb id 时应改道到 TMDb。");
                    Assert.IsTrue(images.Any(image => image.Url == tmdbApi.GetPosterUrl("/series-poster.jpg")?.ToString()), "应返回 TMDb 剧集海报。");
                    Assert.IsFalse(images.Any(image => image.Url?.Contains("douban", StringComparison.OrdinalIgnoreCase) == true), "tmdb-only 自动剧集图片链路不应再泄漏 Douban 图片 URL。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
        }

        [TestMethod]
        public void DefaultScraperPolicy_TmdbOnlyAutomaticImagesReturnEmptyWithoutTmdbFallback()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;

                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "只有豆瓣剧照",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "only-douban-series" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动剧集图片链路在没有 TMDb fallback 时也不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.AreEqual(0, images.Count, "tmdb-only 自动剧集图片链路在没有插件内 TMDb fallback 时应直接返回空集合。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
            }
        }

        [TestMethod]
        public void ManualRemoteImageSearch_TmdbSeries_ReturnsAllImageLanguages()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalEnableTmdb = plugin.Configuration.EnableTmdb;

            try
            {
                plugin.Configuration.EnableTmdb = true;

                var tmdbId = 98765;
                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "多语言图片剧集",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                        { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = CreateManualRemoteImageContextAccessor();
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "手动 TMDb 剧集图片测试不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeriesDetails(tmdbApi, tmdbId, "zh");
                SeedTmdbSeriesImages(tmdbApi, tmdbId, CreateMultilingualSeriesImages());
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();
                    var posters = images.Where(image => image.Type == ImageType.Primary).ToList();
                    var backdrops = images.Where(image => image.Type == ImageType.Backdrop).ToList();
                    var logos = images.Where(image => image.Type == ImageType.Logo).ToList();

                    Assert.AreEqual(4, posters.Count, "手动 RemoteImages 应返回所有 TMDb 海报候选。");
                    Assert.AreEqual(4, backdrops.Count, "手动 RemoteImages 应返回所有 TMDb 背景图候选。");
                    Assert.AreEqual(4, logos.Count, "手动 RemoteImages 应返回所有 TMDb Logo 候选。");
                    AssertContainsLanguages(posters, "海报");
                    AssertContainsLanguages(backdrops, "背景图");
                    AssertContainsLanguages(logos, "Logo");
                    Assert.IsTrue(posters.Any(image => image.Url == tmdbApi.GetPosterUrl("/series-poster-en.jpg")?.ToString()), "海报应使用 TMDb poster URL helper。");
                    Assert.IsTrue(backdrops.Any(image => image.Url == tmdbApi.GetBackdropUrl("/series-backdrop-ja.jpg")?.ToString()), "背景图应使用 TMDb backdrop URL helper。");
                    Assert.IsTrue(logos.Any(image => image.Url == tmdbApi.GetLogoUrl("/series-logo-no-language.png")?.ToString()), "Logo 应使用 TMDb logo URL helper。");
                    Assert.IsTrue(images.All(image => image.ProviderName == MetaSharkPlugin.PluginName), "TMDb 图片 provider name 应保持插件名。");
                    Assert.IsTrue(posters.All(image => image.RatingType == RatingType.Score), "海报评分类型应保持 Score。");
                    Assert.IsTrue(posters.Any(image => image.Width == 1000 && image.Height == 1500 && image.CommunityRating == 8.5 && image.VoteCount == 10), "海报应保留评分、尺寸和投票数。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
        }

        [TestMethod]
        public void ManualRemoteImageSearch_DoubanSeries_AlsoReturnsAllTmdbImageLanguages()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalEnableTmdb = plugin.Configuration.EnableTmdb;

            try
            {
                plugin.Configuration.EnableTmdb = true;

                var tmdbId = 98767;
                var sid = "douban-manual-series";
                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "豆瓣来源多语言剧集",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, sid },
                        { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = CreateManualRemoteImageContextAccessor("series-id");
                var doubanApi = new DoubanApi(this.loggerFactory);
                SeedDoubanSubject(doubanApi, new DoubanSubject
                {
                    Sid = sid,
                    Name = "豆瓣来源多语言剧集",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p98767.jpg",
                    Language = "日语",
                });
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeriesDetails(tmdbApi, tmdbId, "zh");
                SeedTmdbSeriesImages(tmdbApi, tmdbId, CreateMultilingualSeriesImages());
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();
                    var posters = images.Where(image => image.Type == ImageType.Primary).ToList();
                    var tmdbPosters = posters.Where(image => image.Url?.Contains("tmdb", StringComparison.OrdinalIgnoreCase) == true).ToList();

                    Assert.IsTrue(posters.Any(image => image.Url?.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase) == true), "手动 Douban 来源应保留豆瓣中文海报。");
                    Assert.AreEqual(5, posters.Count, "手动 Douban 来源应同时追加 TMDb 全语言海报候选。");
                    AssertContainsLanguages(tmdbPosters, "TMDb 海报");
                    AssertContainsLanguages(images.Where(image => image.Type == ImageType.Backdrop), "背景图");
                    AssertContainsLanguages(images.Where(image => image.Type == ImageType.Logo), "Logo");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
        }

        [TestMethod]
        public void AutomaticTmdbSeriesImages_ReturnSelectedPosterAndBackdropOnly()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalEnableTmdb = plugin.Configuration.EnableTmdb;

            try
            {
                plugin.Configuration.EnableTmdb = true;

                var tmdbId = 98766;
                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "自动图片剧集",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                        { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "自动 TMDb 剧集图片测试不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeriesDetails(tmdbApi, tmdbId, "zh");
                SeedTmdbSeriesImages(tmdbApi, tmdbId, CreateMultilingualSeriesImages());
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();
                    var posters = images.Where(image => image.Type == ImageType.Primary).ToList();
                    var backdrops = images.Where(image => image.Type == ImageType.Backdrop).ToList();

                    Assert.AreEqual(1, posters.Count, "自动刷新应继续只返回当前选中的 TMDb 海报。");
                    Assert.AreEqual(1, backdrops.Count, "自动刷新应继续只返回当前选中的 TMDb 背景图。");
                    Assert.AreEqual(tmdbApi.GetPosterUrl("/series-poster.jpg")?.ToString(), posters[0].Url);
                    Assert.AreEqual(tmdbApi.GetBackdropUrl("/series-backdrop.jpg")?.ToString(), backdrops[0].Url);
                    Assert.AreEqual("zh", posters[0].Language, "自动刷新海报语言应保持首选语言。");
                    Assert.AreEqual("zh", backdrops[0].Language, "自动刷新背景图语言应保持首选语言。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
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

        private static void ConfigureTmdbImageConfig(TmdbApi tmdbApi)
        {
            var tmdbClientField = typeof(TmdbApi).GetField("tmDbClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(tmdbClientField);

            var tmdbClient = tmdbClientField!.GetValue(tmdbApi);
            Assert.IsNotNull(tmdbClient);

            var setConfigMethod = tmdbClient!.GetType().GetMethod("SetConfig", new[] { typeof(TMDbConfig) });
            Assert.IsNotNull(setConfigMethod);

            setConfigMethod!.Invoke(tmdbClient, new object[]
            {
                new TMDbConfig
                {
                    Images = new ConfigImageTypes
                    {
                        BaseUrl = "http://image.tmdb.org/t/p/",
                        SecureBaseUrl = "https://image.tmdb.org/t/p/",
                        PosterSizes = new List<string> { "w500" },
                        BackdropSizes = new List<string> { "w780" },
                        LogoSizes = new List<string> { "w500" },
                        ProfileSizes = new List<string> { "w500" },
                        StillSizes = new List<string> { "w300" },
                    },
                },
            });
        }

        private static IMemoryCache GetTmdbMemoryCache(TmdbApi tmdbApi)
        {
            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "TmdbApi.memoryCache 未定义");

            var memoryCache = memoryCacheField!.GetValue(tmdbApi) as IMemoryCache;
            Assert.IsNotNull(memoryCache, "TmdbApi.memoryCache 不是有效的 IMemoryCache");
            return memoryCache!;
        }

        private static MemoryCache GetDoubanMemoryCache(DoubanApi doubanApi)
        {
            var memoryCacheField = typeof(DoubanApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "DoubanApi.memoryCache 未定义");

            var memoryCache = memoryCacheField!.GetValue(doubanApi) as MemoryCache;
            Assert.IsNotNull(memoryCache, "DoubanApi.memoryCache 不是有效的 MemoryCache");
            return memoryCache!;
        }

        private static void SeedDoubanSubject(DoubanApi doubanApi, DoubanSubject subject)
        {
            var cache = GetDoubanMemoryCache(doubanApi);
            cache.Set($"movie_{subject.Sid}", subject, TimeSpan.FromMinutes(5));
            cache.Set($"photo_{subject.Sid}", new List<DoubanPhoto>(), TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbSeriesDetails(TmdbApi tmdbApi, int tmdbId, string language)
        {
            var cache = GetTmdbMemoryCache(tmdbApi);
            cache.Set(
                $"series-{tmdbId}-{language}-{language}",
                new TvShow
                {
                    Id = tmdbId,
                    Name = "花牌情缘",
                    OriginalName = "ちはやふる",
                    PosterPath = "/series-poster.jpg",
                    BackdropPath = "/series-backdrop.jpg",
                },
                TimeSpan.FromMinutes(5));
            cache.Set(
                $"series-images-{tmdbId}--",
                new ImagesWithId
                {
                    Posters = new List<ImageData> { CreateImageData("/series-poster.jpg", "zh", 1000, 1500) },
                    Backdrops = new List<ImageData> { CreateImageData("/series-backdrop.jpg", "zh", 1920, 1080) },
                    Logos = new List<ImageData> { CreateImageData("/series-logo.png", "zh", 500, 250) },
                },
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbSeriesImages(TmdbApi tmdbApi, int tmdbId, ImagesWithId images)
        {
            var cache = GetTmdbMemoryCache(tmdbApi);
            cache.Set(
                $"series-images-{tmdbId}--",
                images,
                TimeSpan.FromMinutes(5));
        }

        private static ImagesWithId CreateMultilingualSeriesImages()
        {
            return new ImagesWithId
            {
                Posters = new List<ImageData>
                {
                    CreateImageData("/series-poster.jpg", "zh", 1000, 1500),
                    CreateImageData("/series-poster-en.jpg", "en", 1000, 1500),
                    CreateImageData("/series-poster-ja.jpg", "ja", 1000, 1500),
                    CreateImageData("/series-poster-no-language.jpg", null, 1000, 1500),
                },
                Backdrops = new List<ImageData>
                {
                    CreateImageData("/series-backdrop.jpg", "zh", 1920, 1080),
                    CreateImageData("/series-backdrop-en.jpg", "en", 1920, 1080),
                    CreateImageData("/series-backdrop-ja.jpg", "ja", 1920, 1080),
                    CreateImageData("/series-backdrop-no-language.jpg", null, 1920, 1080),
                },
                Logos = new List<ImageData>
                {
                    CreateImageData("/series-logo.png", "zh", 500, 250),
                    CreateImageData("/series-logo-en.png", "en", 500, 250),
                    CreateImageData("/series-logo-ja.png", "ja", 500, 250),
                    CreateImageData("/series-logo-no-language.png", null, 500, 250),
                },
            };
        }

        private static void AssertContainsLanguages(IEnumerable<RemoteImageInfo> images, string imageKind)
        {
            var languages = images.Select(image => image.Language).ToList();
            Assert.IsTrue(languages.Contains("zh"), imageKind + "应包含 zh 图片语言。");
            Assert.IsTrue(languages.Contains("en"), imageKind + "应包含 en 图片语言。");
            Assert.IsTrue(languages.Contains("ja"), imageKind + "应包含 ja 图片语言。");
            Assert.IsTrue(languages.Any(language => language == null), imageKind + "应包含无语言图片。");
        }

        private static IHttpContextAccessor CreateManualRemoteImageContextAccessor(string itemId = "1")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = $"/Items/{itemId}/RemoteImages";
            context.Request.QueryString = new QueryString("?includeAllLanguages=true");
            return new HttpContextAccessor
            {
                HttpContext = context,
            };
        }

        private static ImageData CreateImageData(string filePath, string? language, int width, int height)
        {
            return new ImageData
            {
                FilePath = filePath,
                Iso_639_1 = language,
                Width = width,
                Height = height,
                VoteAverage = 8.5,
                VoteCount = 10,
            };
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
    }
}
