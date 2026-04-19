using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Test.Logging;
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
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.General;

[assembly: DoNotParallelize]

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class MovieProviderTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-movie-provider-tests");
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

        private static void SeedTmdbMovie(TmdbApi tmdbApi, int tmdbId, string language, string title)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"movie-{tmdbId}-{language}-{language}",
                new TMDbLib.Objects.Movies.Movie
                {
                    Id = tmdbId,
                    Title = title,
                    OriginalTitle = title,
                    ImdbId = "tt0000001",
                    Overview = "TMDb seeded movie overview",
                    Tagline = "TMDb seeded movie tagline",
                    ReleaseDate = new DateTime(2007, 3, 3),
                    VoteAverage = 8.6,
                    ProductionCountries = new List<ProductionCountry>(),
                    Genres = new List<Genre>(),
                },
                TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanSubject(DoubanApi doubanApi, DoubanSubject subject)
        {
            var cache = GetDoubanMemoryCache(doubanApi);
            cache.Set($"movie_{subject.Sid}", subject, TimeSpan.FromMinutes(5));
            cache.Set($"celebrities_{subject.Sid}", new List<DoubanCelebrity>(), TimeSpan.FromMinutes(5));
        }

        private static IHttpContextAccessor CreateManualMatchContextAccessor(string itemId = "1")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/Items/RemoteSearch/Apply/{itemId}";
            return new HttpContextAccessor
            {
                HttpContext = context,
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

        [TestMethod]
        public void TestSearch()
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
                var info = new MovieInfo() { Name = "我", MetadataLanguage = "zh" };
                var provider = new MovieProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetSearchResults(info, CancellationToken.None);
                Assert.IsNotNull(result);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetMetadata()
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
                var info = new MovieInfo() { Name = "姥姥的外孙", MetadataLanguage = "zh" };
                var provider = new MovieProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetMetadata(info, CancellationToken.None);
                Assert.IsNotNull(result.Item);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetMetadataAnime()
        {
            var info = new MovieInfo() { Name = "[SAIO-Raws] もののけ姫 Mononoke Hime [BD 1920x1036 HEVC-10bit OPUSx2 AC3]" };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                var provider = new MovieProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetMetadata(info, CancellationToken.None);
                Assert.IsNotNull(result.Item);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetMetadataByTMDB()
        {
            var info = new MovieInfo() { Name = "人生大事", MetadataLanguage = "zh", ProviderIds = new Dictionary<string, string> { { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() }, { MetadataProvider.Tmdb.ToString(), "945664" } } };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                var provider = new MovieProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetMetadata(info, CancellationToken.None);
                Assert.IsNotNull(result.Item);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetMetadataFallsBackToTmdbWhenDoubanBlocked()
        {
            var info = new MovieInfo()
            {
                Name = "秒速5厘米",
                MetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "2043546" },
                    { MetadataProvider.Tmdb.ToString(), "38142" },
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
                    var provider = new MovieProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);
                    Assert.IsNotNull(result.Item);
                    Assert.AreEqual("38142", result.Item.GetProviderId(MetadataProvider.Tmdb));
                    Assert.IsTrue(result.HasMetadata);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests
                    || ex.Message.Contains("429", StringComparison.Ordinal))
                {
                    Assert.Inconclusive("TMDb rate limited (429)." + ex.Message);
                }
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void LegacyDoubanIdDoesNotOverrideTmdbOnly()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;
            var originalEnableTmdb = plugin.Configuration.EnableTmdb;
            var originalEnableTmdbMatch = plugin.Configuration.EnableTmdbMatch;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;
                plugin.Configuration.EnableTmdb = true;
                plugin.Configuration.EnableTmdbMatch = true;

                var info = new MovieInfo()
                {
                    Name = "秒速5厘米",
                    MetadataLanguage = "zh",
                    IsAutomated = true,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "2043546" },
                        { MetadataProvider.Tmdb.ToString(), "38142" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = null,
                };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动电影元数据链路不应再访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                SeedTmdbMovie(tmdbApi, 38142, "zh", "秒速5厘米");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new MovieProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsNotNull(result.Item, "tmdb-only 自动链路在存在有效 TMDb 路由时应改道到 TMDb，而不是返回空结果。");
                    Assert.IsTrue(result.HasMetadata);
                    Assert.AreEqual("38142", result.Item.GetProviderId(MetadataProvider.Tmdb));
                    Assert.AreEqual("秒速5厘米", result.Item.Name);
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
                plugin.Configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public void FilenameDoubanHintDoesNotOverrideTmdbOnly()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;
            var originalEnableTmdb = plugin.Configuration.EnableTmdb;
            var originalEnableTmdbMatch = plugin.Configuration.EnableTmdbMatch;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;
                plugin.Configuration.EnableTmdb = true;
                plugin.Configuration.EnableTmdbMatch = false;

                var info = new MovieInfo()
                {
                    Name = "秒速5厘米",
                    Path = "/test/[douban-2043546] 秒速5厘米.mkv",
                    MetadataLanguage = "zh",
                    IsAutomated = true,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), "38142" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = null,
                };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动电影元数据链路不应因文件名里的 douban hint 回落到 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                SeedTmdbMovie(tmdbApi, 38142, "zh", "秒速5厘米");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new MovieProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsNotNull(result.Item, "tmdb-only 自动链路在存在有效 TMDb 路由时应继续改道到 TMDb。 ");
                    Assert.IsTrue(result.HasMetadata);
                    Assert.AreEqual("38142", result.Item.GetProviderId(MetadataProvider.Tmdb));
                    Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId), "文件名中的 Douban hint 不应被视为 tmdb-only 自动链路的豁免条件。");
                    Assert.AreEqual("秒速5厘米", result.Item.Name);
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
                plugin.Configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public void ManualIdentifyAllowsDoubanUnderTmdbOnly()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;
            var originalEnableTmdbSearch = plugin.Configuration.EnableTmdbSearch;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;
                plugin.Configuration.EnableTmdbSearch = false;

                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = null,
                };
                var doubanApi = new DoubanApi(this.loggerFactory);
                DoubanApiTestHelper.SeedSearchResult(
                    doubanApi,
                    "秒速5厘米",
                    new DoubanSubject
                    {
                        Sid = "2043546",
                        Name = "秒速5厘米",
                        OriginalName = "秒速5センチメートル",
                        Year = 2007,
                        Category = "电影",
                        Genre = "动画",
                        Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000000.webp",
                    });
                var tmdbApi = new TmdbApi(this.loggerFactory);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new MovieProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var results = (await provider.GetSearchResults(new MovieInfo { Name = "秒速5厘米", MetadataLanguage = "zh" }, CancellationToken.None)).ToList();

                    Assert.IsTrue(results.Any(r => r.ProviderIds.TryGetValue(BaseProvider.DoubanProviderId, out var sid) && sid == "2043546"), "tmdb-only 不应误伤手动搜索返回 Douban 候选。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
                plugin.Configuration.EnableTmdbSearch = originalEnableTmdbSearch;
            }
        }

        [TestMethod]
        public void ManualMatchAllowsDoubanUnderTmdbOnly()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;
            var originalEnableTmdb = plugin.Configuration.EnableTmdb;
            var originalEnableTmdbMatch = plugin.Configuration.EnableTmdbMatch;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;
                plugin.Configuration.EnableTmdb = false;
                plugin.Configuration.EnableTmdbMatch = false;

                var info = new MovieInfo
                {
                    Name = "秒速5厘米",
                    MetadataLanguage = "zh",
                    IsAutomated = true,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "2043546" },
                        { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_2043546" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = CreateManualMatchContextAccessor();
                var doubanApi = new DoubanApi(this.loggerFactory);
                SeedDoubanSubject(
                    doubanApi,
                    new DoubanSubject
                    {
                        Sid = "2043546",
                        Name = "秒速5厘米",
                        OriginalName = "秒速5センチメートル",
                        Year = 2007,
                        Category = "电影",
                        Genre = "动画",
                        Intro = "豆瓣手动匹配详情",
                        Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000000.webp",
                        Screen = "2007-03-03",
                    });
                var tmdbApi = new TmdbApi(this.loggerFactory);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new MovieProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsNotNull(result.Item, "显式手动匹配语义下仍应允许使用 Douban 详情。" );
                    Assert.IsTrue(result.HasMetadata);
                    Assert.AreEqual("2043546", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
                    Assert.AreEqual("秒速5厘米", result.Item.Name);
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
                plugin.Configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task MovieProviderLog_GetSearchResults_UsesChineseSummary()
        {
            var providerLogger = new Mock<ILogger>();
            providerLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var providerLoggerFactory = new Mock<ILoggerFactory>();
            providerLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(providerLogger.Object);

            using var apiLoggerFactory = LoggerFactory.Create(builder => { });
            var provider = new MovieProvider(
                new DefaultHttpClientFactory(),
                providerLoggerFactory.Object,
                new Mock<ILibraryManager>().Object,
                new Mock<IHttpContextAccessor>().Object,
                new DoubanApi(apiLoggerFactory),
                new TmdbApi(apiLoggerFactory),
                new OmdbApi(apiLoggerFactory),
                new ImdbApi(apiLoggerFactory));

            var results = (await provider.GetSearchResults(new MovieInfo { Name = string.Empty }, CancellationToken.None).ConfigureAwait(false)).ToList();

            Assert.AreEqual(0, results.Count);
            LogAssert.AssertLoggedOnce(
                providerLogger,
                LogLevel.Information,
                expectException: false,
                originalFormatContains: "[MetaShark] {Message}",
                messageContains: new[] { "开始搜索电影候选. name: " });
        }

        [TestMethod]
        public async Task MovieProviderLog_GetMetadataByTmdb_UsesChineseSummary()
        {
            var providerLogger = new Mock<ILogger>();
            providerLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var providerLoggerFactory = new Mock<ILoggerFactory>();
            providerLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(providerLogger.Object);

            using var apiLoggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(apiLoggerFactory);
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");

            var provider = new MovieProvider(
                new DefaultHttpClientFactory(),
                providerLoggerFactory.Object,
                new Mock<ILibraryManager>().Object,
                new Mock<IHttpContextAccessor>().Object,
                new DoubanApi(apiLoggerFactory),
                tmdbApi,
                new OmdbApi(apiLoggerFactory),
                new ImdbApi(apiLoggerFactory));

            var result = await provider.GetMetadata(
                new MovieInfo
                {
                    Name = "示例电影",
                    MetadataLanguage = "zh-CN",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetaSharkPlugin.ProviderId, "Tmdb_123" },
                        { MetadataProvider.Tmdb.ToString(), "123" },
                    },
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            LogAssert.AssertLoggedAtLeastOnce(
                providerLogger,
                LogLevel.Information,
                expectException: false,
                originalFormatContains: "[MetaShark] {Message}",
                messageContains: new[]
                {
                    "开始获取电影元数据. name: 示例电影",
                    "metaSource: Tmdb",
                    "enableTmdb: True",
                });
            LogAssert.AssertLoggedOnce(
                providerLogger,
                LogLevel.Information,
                expectException: false,
                originalFormatContains: "[MetaShark] {Message}",
                messageContains: new[] { "通过 TMDb 获取电影元数据. tmdbId: \"123\"" });
        }
    }
}
