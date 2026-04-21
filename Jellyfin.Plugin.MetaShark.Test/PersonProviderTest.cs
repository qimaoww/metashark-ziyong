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
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.General;
using TmdbPerson = TMDbLib.Objects.People.Person;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class PersonProviderTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-person-provider-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
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
        public void TestGetMetadata()
        {
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            Task.Run(async () =>
            {
                var info = new PersonLookupInfo() { ProviderIds = new Dictionary<string, string>() { { BaseProvider.DoubanProviderId, "27257290" } } };
                var provider = new PersonProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetMetadata(info, CancellationToken.None);
                Assert.IsNotNull(result);

                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetMetadataByTmdb()
        {
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            Task.Run(async () =>
            {
                var info = new PersonLookupInfo() { ProviderIds = new Dictionary<string, string>() { { MetadataProvider.Tmdb.ToString(), "78871" } } };
                var provider = new PersonProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetMetadata(info, CancellationToken.None);
                Assert.IsNotNull(result);

                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void DefaultScraperPolicy_TmdbOnlyAutomaticMetadataSkipsDoubanAndUsesTmdbFallback()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;

                var info = new PersonLookupInfo
                {
                    Name = "周迅",
                    MetadataLanguage = "zh",
                    IsAutomated = true,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "27257290" },
                        { MetadataProvider.Tmdb.ToString(), "287" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动人物元数据链路不应再访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                SeedTmdbPerson(tmdbApi, 287);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new PersonProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsTrue(result.HasMetadata, "tmdb-only 自动人物元数据链路在存在有效 TMDb id 时应改道到 TMDb。");
                    Assert.IsNotNull(result.Item);
                    Assert.AreEqual("287", result.Item.GetProviderId(MetadataProvider.Tmdb));
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
            }
        }

        [TestMethod]
        public void DefaultScraperPolicy_TmdbOnlyAutomaticMetadataReturnsEmptyWithoutTmdbFallback()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;

                var info = new PersonLookupInfo
                {
                    Name = "周迅",
                    MetadataLanguage = "zh",
                    IsAutomated = true,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "27257290" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动人物元数据链路在没有 TMDb fallback 时也不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new PersonProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsFalse(result.HasMetadata, "tmdb-only 自动人物元数据链路在没有插件内 TMDb fallback 时应直接返回空结果。");
                    Assert.IsNull(result.Item);
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
            }
        }

        [TestMethod]
        public void ManualSearchAllowsDoubanUnderTmdbOnly()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;

                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = new DoubanApi(this.loggerFactory);
                SeedDoubanCelebritySearchResult(doubanApi, "周迅", new DoubanCelebrity
                {
                    Id = "27257290",
                    Name = "周迅",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000000.webp",
                });
                var tmdbApi = new TmdbApi(this.loggerFactory);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new PersonProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var results = (await provider.GetSearchResults(new PersonLookupInfo { Name = "周迅", MetadataLanguage = "zh" }, CancellationToken.None)).ToList();

                    Assert.IsTrue(results.Any(result => result.ProviderIds.TryGetValue(BaseProvider.DoubanProviderId, out var value) && value == "27257290"), "tmdb-only 不应误伤真正的手动人物搜索路径。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
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

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;

                var info = new PersonLookupInfo
                {
                    Name = "周迅",
                    MetadataLanguage = "zh",
                    IsAutomated = true,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "27257290" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = CreateManualMatchContextAccessor();
                var doubanApi = new DoubanApi(this.loggerFactory);
                SeedDoubanCelebrity(
                    doubanApi,
                    new DoubanCelebrity
                    {
                        Id = "27257290",
                        Name = "周迅",
                        Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000000.webp",
                        Intro = "豆瓣人物详情",
                    });
                var tmdbApi = new TmdbApi(this.loggerFactory);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new PersonProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsTrue(result.HasMetadata, "显式手动匹配语义下仍应允许使用 Douban 人物详情。");
                    Assert.IsNotNull(result.Item);
                    Assert.AreEqual("27257290", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
            }
        }

        [TestMethod]
        public async Task TmdbApi_GetPersonAsync_SeparatesLanguageSpecificCacheEntries()
        {
            EnsurePluginInstance();

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedTmdbPerson(tmdbApi, 1001, biography: "中文人物简介", language: "zh-cn");
            SeedTmdbPerson(tmdbApi, 1001, biography: "English person biography", language: "en-us");

            var zhPerson = await tmdbApi.GetPersonAsync(1001, "zh-CN", null, CancellationToken.None).ConfigureAwait(false);
            var enPerson = await tmdbApi.GetPersonAsync(1001, "en-US", null, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(zhPerson);
            Assert.IsNotNull(enPerson);
            Assert.AreEqual("中文人物简介", zhPerson.Biography);
            Assert.AreEqual("English person biography", enPerson.Biography);
            Assert.AreNotEqual(zhPerson.Biography, enPerson.Biography, "同一个 TMDb 人物 id 的不同语言缓存不应串用。");
        }

        [TestMethod]
        public async Task TmdbApi_GetPersonAsync_KeepsNeutralWrapperCompatible()
        {
            EnsurePluginInstance();

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedTmdbPerson(tmdbApi, 1002, biography: "Neutral biography");
            SeedTmdbPerson(tmdbApi, 1002, biography: "简体中文简介", language: "zh-CN");

            var neutralPerson = await tmdbApi.GetPersonAsync(1002, CancellationToken.None).ConfigureAwait(false);
            var zhPerson = await tmdbApi.GetPersonAsync(1002, "zh", "CN", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(neutralPerson);
            Assert.IsNotNull(zhPerson);
            Assert.AreEqual("Neutral biography", neutralPerson.Biography, "旧的无语言包装方法应继续命中 neutral 人物缓存。");
            Assert.AreEqual("简体中文简介", zhPerson.Biography, "带语言的人物详情应继续读取各自语言缓存。");
        }

        [TestMethod]
        public async Task TmdbApi_GetPersonTranslationsAsync_ReturnsSeededTranslations()
        {
            EnsurePluginInstance();

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedTmdbPersonTranslations(
                tmdbApi,
                1003,
                new Translation
                {
                    Iso_639_1 = "zh",
                    Iso_3166_1 = "CN",
                    Name = "简体中文",
                },
                new Translation
                {
                    Iso_639_1 = "en",
                    Iso_3166_1 = "US",
                    Name = "English",
                });

            var translations = await tmdbApi.GetPersonTranslationsAsync(1003, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(translations);
            Assert.AreEqual(1003, translations.Id);
            Assert.IsNotNull(translations.Translations);
            Assert.AreEqual(2, translations.Translations!.Count);
            Assert.AreEqual("CN", translations.Translations[0].Iso_3166_1);
            Assert.AreEqual("English", translations.Translations[1].Name);
        }

        [TestMethod]
        public async Task GetMetadataByTmdb_PrefersZhCnBiographyAndKeepsNameUntouched()
        {
            EnsurePluginInstance();

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedTmdbPerson(tmdbApi, 2001, biography: "Neutral biography");
            SeedTmdbPerson(tmdbApi, 2001, biography: "这个角色很厉害，也令人惊讶。", language: "zh", countryCode: "CN");
            SeedTmdbPerson(tmdbApi, 2001, biography: "这个角色很厉害，但是不应走到 zh-Hans。", language: "zh-Hans");
            SeedTmdbPerson(tmdbApi, 2001, biography: "这个角色很厉害，但是不应走到 zh。", language: "zh");

            var provider = this.CreateTmdbPersonProvider(tmdbApi);
            var result = await provider.GetMetadataByTmdb(2001, new PersonLookupInfo { Name = "Original Name" }, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual("这个角色很厉害，也令人惊讶。", result.Item.Overview, "TMDb 人物 biography 应优先命中 zh-CN。");
            Assert.IsNull(result.Item.Name, "PersonProvider 不应主动覆盖 item.Name。");
            Assert.AreEqual("2001", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("https://www.example.com/person", result.Item.HomePageUrl);
            CollectionAssert.AreEqual(new[] { "浙江 衢州" }, result.Item.ProductionLocations!.ToArray());
        }

        [TestMethod]
        public async Task GetMetadataByTmdb_UsesZhHansBiographyWhenZhCnIsUnavailable()
        {
            EnsurePluginInstance();

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedTmdbPerson(tmdbApi, 2002, biography: "Neutral biography");
            SeedTmdbPerson(tmdbApi, 2002, biography: "這個角色很厲害，也令人驚訝。", language: "zh", countryCode: "CN");
            SeedTmdbPerson(tmdbApi, 2002, biography: "这个角色很厉害，也令人惊讶。", language: "zh-Hans");

            var provider = this.CreateTmdbPersonProvider(tmdbApi);
            var result = await provider.GetMetadataByTmdb(2002, new PersonLookupInfo { Name = "Original Name" }, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual("这个角色很厉害，也令人惊讶。", result.Item.Overview, "zh-CN 候选不合格时应继续尝试 zh-Hans。");
        }

        [TestMethod]
        public async Task GetMetadataByTmdb_UsesGenericZhBiographyOnlyWhenTextIsSimplifiedChinese()
        {
            EnsurePluginInstance();

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedTmdbPerson(tmdbApi, 2003, biography: "Neutral biography");
            SeedTmdbPerson(tmdbApi, 2003, biography: "English biography", language: "zh", countryCode: "CN");
            SeedTmdbPerson(tmdbApi, 2003, biography: "這個角色很厲害，也令人驚訝。", language: "zh-Hans");
            SeedTmdbPerson(tmdbApi, 2003, biography: "这个角色很厉害，也令人惊讶。", language: "zh");

            var provider = this.CreateTmdbPersonProvider(tmdbApi);
            var result = await provider.GetMetadataByTmdb(2003, new PersonLookupInfo { Name = "Original Name" }, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual("这个角色很厉害，也令人惊讶。", result.Item.Overview, "只有当通用 zh 文本本身可接受为简体中文时，才允许写入 Overview。");
        }

        [TestMethod]
        public async Task GetMetadataByTmdb_DoesNotWriteOverviewWhenOnlyTraditionalOrNonChineseBiographyExists()
        {
            EnsurePluginInstance();

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedTmdbPerson(tmdbApi, 2004, biography: "Neutral biography");
            SeedTmdbPerson(tmdbApi, 2004, biography: "這個角色很厲害，也令人驚訝。", language: "zh", countryCode: "CN");
            SeedTmdbPerson(tmdbApi, 2004, biography: "English biography", language: "zh-Hans");
            SeedTmdbPerson(tmdbApi, 2004, biography: "這個角色很厲害，也令人驚訝。", language: "zh");

            var provider = this.CreateTmdbPersonProvider(tmdbApi);
            var result = await provider.GetMetadataByTmdb(2004, new PersonLookupInfo { Name = "Original Name" }, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.IsNull(result.Item.Overview, "TMDb 人物路径在只有繁体或非中文 biography 时不应写入 Overview，也不能回退 neutral/English。");
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

        private PersonProvider CreateTmdbPersonProvider(TmdbApi tmdbApi)
        {
            return new PersonProvider(
                new DefaultHttpClientFactory(),
                this.loggerFactory,
                new Mock<ILibraryManager>().Object,
                new HttpContextAccessor { HttpContext = null },
                new DoubanApi(this.loggerFactory),
                tmdbApi,
                new OmdbApi(this.loggerFactory),
                new ImdbApi(this.loggerFactory));
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

        private static void SeedTmdbPerson(TmdbApi tmdbApi, int tmdbId, string biography = "TMDb seeded biography", string? language = null, string? countryCode = null)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                GetTmdbPersonCacheKey(tmdbId, language ?? string.Empty, countryCode),
                new TmdbPerson
                {
                    Id = tmdbId,
                    Biography = biography,
                    Homepage = "https://www.example.com/person",
                    Birthday = new DateTime(1974, 10, 18),
                    PlaceOfBirth = "浙江 衢州",
                },
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbPersonTranslations(TmdbApi tmdbApi, int tmdbId, params Translation[] translations)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                GetTmdbPersonTranslationsCacheKey(tmdbId),
                new TranslationsContainer
                {
                    Id = tmdbId,
                    Translations = translations.ToList(),
                },
                TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanCelebrity(DoubanApi doubanApi, DoubanCelebrity celebrity)
        {
            GetDoubanMemoryCache(doubanApi).Set($"personage_{celebrity.Id}", celebrity, TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanCelebritySearchResult(DoubanApi doubanApi, string keyword, params DoubanCelebrity[] celebrities)
        {
            GetDoubanMemoryCache(doubanApi).Set($"search_celebrity_{keyword}", celebrities.ToList(), TimeSpan.FromMinutes(5));
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

        private static string GetTmdbPersonCacheKey(int tmdbId, string language, string? countryCode)
        {
            var cacheKeyMethod = typeof(TmdbApi).GetMethod("GetPersonCacheKey", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheKeyMethod, "TmdbApi.GetPersonCacheKey 未定义");

            var cacheKey = cacheKeyMethod!.Invoke(null, new object?[] { tmdbId, language, countryCode }) as string;
            Assert.IsFalse(string.IsNullOrEmpty(cacheKey), "TmdbApi.GetPersonCacheKey 返回了无效缓存键");
            return cacheKey!;
        }

        private static string GetTmdbPersonTranslationsCacheKey(int tmdbId)
        {
            var cacheKeyMethod = typeof(TmdbApi).GetMethod("GetPersonTranslationsCacheKey", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheKeyMethod, "TmdbApi.GetPersonTranslationsCacheKey 未定义");

            var cacheKey = cacheKeyMethod!.Invoke(null, new object?[] { tmdbId }) as string;
            Assert.IsFalse(string.IsNullOrEmpty(cacheKey), "TmdbApi.GetPersonTranslationsCacheKey 返回了无效缓存键");
            return cacheKey!;
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
