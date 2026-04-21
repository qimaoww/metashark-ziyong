using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Client;
using TMDbLib.Objects.General;
using TmdbGenre = TMDbLib.Objects.General.Genre;
using TmdbMovie = TMDbLib.Objects.Movies.Movie;
using TmdbPerson = TMDbLib.Objects.People.Person;
using TmdbTranslationData = TMDbLib.Objects.General.TranslationData;

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

        private static void SeedTmdbMovie(TmdbApi tmdbApi, int tmdbId, string language, string title)
        {
            SeedTmdbMovie(
                tmdbApi,
                tmdbId,
                language,
                new TmdbMovie
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
                    Genres = new List<TmdbGenre>(),
                });
        }

        private static void SeedTmdbMovie(TmdbApi tmdbApi, int tmdbId, string language, TmdbMovie movie)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"movie-{tmdbId}-{language}-{language}",
                movie,
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbPerson(TmdbApi tmdbApi, int tmdbId, string? name, string? language = null, string? countryCode = null)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                GetTmdbPersonCacheKey(tmdbId, language ?? string.Empty, countryCode),
                new TmdbPerson
                {
                    Id = tmdbId,
                    Name = name,
                    Biography = "TMDb seeded biography",
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

        private static Translation CreatePersonTranslation(string language, string? locale, string? translatedName)
        {
            return new Translation
            {
                Iso_639_1 = language,
                Iso_3166_1 = locale,
                Data = new TmdbTranslationData
                {
                    Name = translatedName,
                },
            };
        }

        private static void SetTmdbMovieCredits(
            TmdbMovie movie,
            IEnumerable<Dictionary<string, object?>> castEntries,
            IEnumerable<Dictionary<string, object?>> crewEntries)
        {
            var creditsProperty = typeof(TmdbMovie).GetProperty("Credits");
            Assert.IsNotNull(creditsProperty, "TMDb movie Credits 属性未定义");

            var credits = Activator.CreateInstance(creditsProperty!.PropertyType);
            Assert.IsNotNull(credits, "无法创建 TMDb movie Credits 实例");

            SetTmdbCreditList(credits!, "Cast", castEntries);
            SetTmdbCreditList(credits!, "Crew", crewEntries);
            creditsProperty.SetValue(movie, credits);
        }

        private static void SetTmdbCreditList(object credits, string propertyName, IEnumerable<Dictionary<string, object?>> entries)
        {
            var listProperty = credits.GetType().GetProperty(propertyName);
            Assert.IsNotNull(listProperty, $"TMDb Credits.{propertyName} 属性未定义");

            var itemType = listProperty!.PropertyType.GetGenericArguments().Single();
            var listType = typeof(List<>).MakeGenericType(itemType);
            var list = Activator.CreateInstance(listType) as IList;
            Assert.IsNotNull(list, $"无法创建 TMDb Credits.{propertyName} 列表实例");

            foreach (var entry in entries)
            {
                var item = Activator.CreateInstance(itemType);
                Assert.IsNotNull(item, $"无法创建 TMDb Credits.{propertyName} 条目实例");

                foreach (var pair in entry)
                {
                    var property = itemType.GetProperty(pair.Key);
                    Assert.IsNotNull(property, $"TMDb credit 条目缺少属性 {pair.Key}");
                    property!.SetValue(item, pair.Value);
                }

                list!.Add(item);
            }

            listProperty.SetValue(credits, list);
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

        private static PersonInfo GetPersonByTmdbId(IEnumerable<PersonInfo> people, int tmdbId)
        {
            return people.Single(person => person.ProviderIds != null
                && person.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var providerId)
                && string.Equals(providerId, tmdbId.ToString(), StringComparison.Ordinal));
        }

        private static bool HasPersonByTmdbId(IEnumerable<PersonInfo> people, int tmdbId)
        {
            return people.Any(person => person.ProviderIds != null
                && person.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var providerId)
                && string.Equals(providerId, tmdbId.ToString(), StringComparison.Ordinal));
        }

        private static int GetTmdbProviderId(PersonInfo person)
        {
            Assert.IsNotNull(person.ProviderIds);
            Assert.IsTrue(person.ProviderIds!.TryGetValue(MetadataProvider.Tmdb.ToString(), out var providerId), "PersonInfo 缺少 TMDb provider id");
            Assert.IsTrue(int.TryParse(providerId, out var tmdbId), "PersonInfo 的 TMDb provider id 不是有效整数");
            return tmdbId;
        }

        private static Dictionary<string, object?> CreateCastCredit(int tmdbId, string rawName, string character, int order, string? profilePath = null)
        {
            var entry = new Dictionary<string, object?>
            {
                ["Id"] = tmdbId,
                ["Name"] = rawName,
                ["Character"] = character,
                ["Order"] = order,
            };

            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                entry["ProfilePath"] = profilePath;
            }

            return entry;
        }

        private static void SeedBlankExactZhCnCastPerson(TmdbApi tmdbApi, int tmdbId)
        {
            SeedTmdbPerson(tmdbApi, tmdbId, string.Empty, language: "zh-CN");
            SeedTmdbPersonTranslations(tmdbApi, tmdbId, CreatePersonTranslation("zh", "CN", string.Empty));
        }

        private static void SeedExactZhCnCastPerson(TmdbApi tmdbApi, int tmdbId, string exactZhCnName)
        {
            SeedTmdbPerson(tmdbApi, tmdbId, exactZhCnName, language: "zh-CN");
        }

        private static void SeedDoubanSubject(DoubanApi doubanApi, DoubanSubject subject)
        {
            var cache = GetDoubanMemoryCache(doubanApi);
            cache.Set($"movie_{subject.Sid}", subject, TimeSpan.FromMinutes(5));
            cache.Set($"celebrities_{subject.Sid}", new List<DoubanCelebrity>(), TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanCelebrities(DoubanApi doubanApi, string sid, IEnumerable<DoubanCelebrity> celebrities)
        {
            var cache = GetDoubanMemoryCache(doubanApi);
            cache.Set($"celebrities_{sid}", celebrities.ToList(), TimeSpan.FromMinutes(5));
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
            SeedTmdbMovie(tmdbApi, 38142, "zh", "秒速5厘米");
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
        public async Task GetMetadataByDouban_BackfillsTmdbOnlyItemLevelPeople_WhenTmdbRouteIsAvailable()
        {
            var info = new MovieInfo
            {
                Name = "豆瓣示例电影",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "movie-douban-100" },
                    { MetadataProvider.Tmdb.ToString(), "9301" },
                    { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_movie-douban-100" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            SeedDoubanSubject(
                doubanApi,
                new DoubanSubject
                {
                    Sid = "movie-douban-100",
                    Name = "豆瓣示例电影",
                    OriginalName = "Douban Sample Movie",
                    Year = 2024,
                    Rating = 8.6f,
                    Genre = "剧情 / 动画",
                    Intro = "豆瓣电影简介",
                    Screen = "2024-03-03",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000000.webp",
                });
            SeedDoubanCelebrities(
                doubanApi,
                "movie-douban-100",
                new[]
                {
                    new DoubanCelebrity
                    {
                        Id = "douban-director-1",
                        Name = "豆瓣导演",
                        Role = "导演",
                        Img = "https://img9.doubanio.com/view/celebrity/raw/public/p0000000001.webp",
                    },
                    new DoubanCelebrity
                    {
                        Id = "douban-actor-1",
                        Name = "豆瓣演员",
                        Role = "演员",
                        Img = "https://img9.doubanio.com/view/celebrity/raw/public/p0000000002.webp",
                    },
                });
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            var seededMovie = new TmdbMovie
            {
                Id = 9301,
                Title = "TMDb 占位电影",
                OriginalTitle = "TMDb Placeholder Movie",
                Overview = "TMDb seeded movie overview",
                ReleaseDate = new DateTime(2024, 3, 3),
                VoteAverage = 7.1,
                ProductionCountries = new List<ProductionCountry>(),
                Genres = new List<TmdbGenre>(),
            };
            SetTmdbMovieCredits(
                seededMovie,
                new[]
                {
                    CreateCastCredit(1301, "Raw Actor A", "角色A", 0, "/actor-a.jpg"),
                    CreateCastCredit(1302, string.Empty, "角色B", 1, "/actor-b.jpg"),
                },
                new[]
                {
                    new Dictionary<string, object?> { ["Id"] = 2301, ["Name"] = string.Empty, ["Department"] = "Production", ["Job"] = "Director", ["ProfilePath"] = "/director-a.jpg" },
                    new Dictionary<string, object?> { ["Id"] = 2399, ["Name"] = "Ignored Crew", ["Department"] = "Art", ["Job"] = "Art Direction" },
                });
            SeedTmdbMovie(tmdbApi, 9301, "zh-CN", seededMovie);
            SeedTmdbPerson(tmdbApi, 1301, "这个演员甲", language: "zh-CN");
            SeedBlankExactZhCnCastPerson(tmdbApi, 1302);
            SeedTmdbPerson(tmdbApi, 2301, string.Empty, language: "zh-CN");
            SeedTmdbPersonTranslations(tmdbApi, 2301, CreatePersonTranslation("zh", "CN", "这个导演甲"));
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var provider = new MovieProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("豆瓣示例电影", result.Item.Name);
            Assert.AreEqual("Douban Sample Movie", result.Item.OriginalTitle);
            Assert.AreEqual("豆瓣电影简介", result.Item.Overview);
            Assert.AreEqual(2024, result.Item.ProductionYear);
            Assert.IsTrue(result.Item.CommunityRating > 8.5f && result.Item.CommunityRating < 8.7f, "Douban 路径仍应保留评分等非 people 元数据。 ");
            Assert.AreEqual("movie-douban-100", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.IsNotNull(result.People);
            var people = result.People!;
            Assert.AreEqual(2, people.Count, "Douban 路径拿到 tmdbId 后，应补入通过 strict zh-CN helper 接受的 TMDb-only people。 ");
            Assert.IsFalse(result.People?.Any(person => person.ProviderIds != null
                && person.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId)) ?? false, "Douban 元数据路径即使拿到了 tmdb 路由，也不应再把 Douban celebrities 写入 movie item-level people。 ");
            Assert.IsTrue(people.All(person => person.ProviderIds != null && person.ProviderIds.ContainsKey(MetadataProvider.Tmdb.ToString())), "Douban 路径下的 movie item-level people 必须保持 TMDb-only。 ");
            Assert.AreEqual("这个演员甲", GetPersonByTmdbId(people, 1301).Name);
            Assert.AreEqual("角色A", GetPersonByTmdbId(people, 1301).Role);
            Assert.AreEqual("这个导演甲", GetPersonByTmdbId(people, 2301).Name);
            Assert.AreEqual(PersonKind.Director, GetPersonByTmdbId(people, 2301).Type);
            Assert.IsFalse(HasPersonByTmdbId(people, 1302), "strict zh-CN helper 在缺少精确 zh-CN detail/translation 时不应把 raw-only 演员带入 Douban 回填结果。 ");
            Assert.IsFalse(people.Any(person => string.Equals(person.Name, "豆瓣导演", StringComparison.Ordinal) || string.Equals(person.Name, "豆瓣演员", StringComparison.Ordinal)), "Douban 路径回填 people 时只应接受 TMDb people，不应把 Douban celebrity 名字写回 item-level people。 ");
        }

        [TestMethod]
        public async Task GetMetadataByDouban_KeepsItemLevelPeopleEmpty_WhenTmdbRouteIsUnavailable()
        {
            var info = new MovieInfo
            {
                Name = "豆瓣示例电影",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "movie-douban-101" },
                    { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_movie-douban-101" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            SeedDoubanSubject(
                doubanApi,
                new DoubanSubject
                {
                    Sid = "movie-douban-101",
                    Name = "豆瓣无 TMDb 电影",
                    OriginalName = "Douban No TMDb Movie",
                    Year = 0,
                    Rating = 7.8f,
                    Genre = "剧情 / 悬疑",
                    Intro = "豆瓣无 TMDb 简介",
                    Screen = "2024-04-04",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000006.webp",
                });
            SeedDoubanCelebrities(
                doubanApi,
                "movie-douban-101",
                new[]
                {
                    new DoubanCelebrity
                    {
                        Id = "douban-director-2",
                        Name = "豆瓣导演乙",
                        Role = "导演",
                        Img = "https://img9.doubanio.com/view/celebrity/raw/public/p0000000007.webp",
                    },
                    new DoubanCelebrity
                    {
                        Id = "douban-actor-2",
                        Name = "豆瓣演员乙",
                        Role = "演员",
                        Img = "https://img9.doubanio.com/view/celebrity/raw/public/p0000000008.webp",
                    },
                });
            var tmdbApi = new TmdbApi(this.loggerFactory);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var provider = new MovieProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("豆瓣无 TMDb 电影", result.Item.Name);
            Assert.AreEqual("Douban No TMDb Movie", result.Item.OriginalTitle);
            Assert.AreEqual("豆瓣无 TMDb 简介", result.Item.Overview);
            Assert.IsTrue(result.Item.CommunityRating > 7.7f && result.Item.CommunityRating < 7.9f, "Douban 路径在拿不到 tmdbId 时仍应保留主元数据字段。 ");
            Assert.IsNull(result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsTrue(result.People == null || result.People.Count == 0, "拿不到 tmdbId 时，movie item-level people 应保持为空。 ");
            Assert.IsFalse(result.People?.Any(person => person.ProviderIds != null
                && person.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId)) ?? false, "拿不到 tmdbId 时，也不应回退写入带 Douban provider id 的 people。 ");
        }

        [TestMethod]
        public async Task GetMetadataByTMDB_PrefersExactZhCnPeopleAndRejectsRawFallbackWhenStrictSourceMissing()
        {
            var info = new MovieInfo
            {
                Name = "示例电影",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetaSharkPlugin.ProviderId, "Tmdb_9100" },
                    { MetadataProvider.Tmdb.ToString(), "9100" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var seededMovie = new TmdbMovie
            {
                Id = 9100,
                Title = "示例电影",
                OriginalTitle = "Sample Movie",
                ImdbId = "tt0910000",
                Overview = "TMDb seeded movie overview",
                Tagline = "TMDb seeded movie tagline",
                ReleaseDate = new DateTime(2024, 1, 1),
                VoteAverage = 8.3,
                ProductionCountries = new List<ProductionCountry>(),
                Genres = new List<TmdbGenre>(),
            };
            SetTmdbMovieCredits(
                seededMovie,
                new[]
                {
                    new Dictionary<string, object?> { ["Id"] = 1001, ["Name"] = "这个演员原名", ["Character"] = "角色A", ["Order"] = 0, ["ProfilePath"] = "/actor-raw.jpg" },
                    new Dictionary<string, object?> { ["Id"] = 1002, ["Name"] = "這個演員原名", ["Character"] = "角色B", ["Order"] = 1, ["ProfilePath"] = "/actor-zh-cn.jpg" },
                    new Dictionary<string, object?> { ["Id"] = 1003, ["Name"] = "Actor Hans Raw", ["Character"] = "角色C", ["Order"] = 2, ["ProfilePath"] = "/actor-zh-hans.jpg" },
                    new Dictionary<string, object?> { ["Id"] = 1004, ["Name"] = string.Empty, ["Character"] = "角色D", ["Order"] = 3 },
                    new Dictionary<string, object?> { ["Id"] = 1005, ["Name"] = string.Empty, ["Character"] = "角色E", ["Order"] = 4 },
                    new Dictionary<string, object?> { ["Id"] = 1006, ["Name"] = string.Empty, ["Character"] = "角色F", ["Order"] = 5 },
                },
                new[]
                {
                    new Dictionary<string, object?> { ["Id"] = 2001, ["Name"] = "這個導演原名", ["Department"] = "Production", ["Job"] = "Director", ["ProfilePath"] = "/director.jpg" },
                    new Dictionary<string, object?> { ["Id"] = 2002, ["Name"] = "Producer Raw", ["Department"] = "Production", ["Job"] = "Producer", ["ProfilePath"] = "/producer.jpg" },
                    new Dictionary<string, object?> { ["Id"] = 2003, ["Name"] = string.Empty, ["Department"] = "Writing", ["Job"] = "Screenplay", ["ProfilePath"] = "/writer.jpg" },
                    new Dictionary<string, object?> { ["Id"] = 2004, ["Name"] = string.Empty, ["Department"] = "Production", ["Job"] = "Director", ["ProfilePath"] = "/fallback-director.jpg" },
                    new Dictionary<string, object?> { ["Id"] = 2999, ["Name"] = "Ignored Crew", ["Department"] = "Art", ["Job"] = "Art Direction" },
                });
            SeedTmdbMovie(tmdbApi, 9100, "zh-CN", seededMovie);

            SeedTmdbPerson(tmdbApi, 1001, "这个演员大陆候选", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1002, "這個演員原名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1003, "Actor Hans Raw", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1004, "这个演员详情名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1005, string.Empty, language: "zh-CN");
            SeedTmdbPersonTranslations(tmdbApi, 1005, CreatePersonTranslation("zh", "CN", "这个演员翻译名"));
            SeedBlankExactZhCnCastPerson(tmdbApi, 1006);

            SeedTmdbPerson(tmdbApi, 2001, "这个导演大陆名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 2002, "這個製片人繁體名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 2003, string.Empty, language: "zh-CN");
            SeedTmdbPersonTranslations(tmdbApi, 2003, CreatePersonTranslation("zh", "CN", "这个编剧翻译名"));
            SeedBlankExactZhCnCastPerson(tmdbApi, 2004);

            var provider = new MovieProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.People);
            Assert.IsFalse(result.Item.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false, "provider 不应在返回 metadata result 时提前写入内部 people refresh state。 ");
            Assert.AreEqual(8, result.People.Count, "没有可接受的精确 zh-CN 源值时，不应写入电影 item-level people。 ");

            var actorExactZhCn = GetPersonByTmdbId(result.People, 1001);
            Assert.AreEqual("这个演员大陆候选", actorExactZhCn.Name, "strict item-level path 命中精确 zh-CN detail 时应优先返回它，而不是 raw credits 名称。 ");
            Assert.AreEqual("角色A", actorExactZhCn.Role);
            Assert.AreEqual(PersonKind.Actor, actorExactZhCn.Type);
            Assert.AreEqual(0, actorExactZhCn.SortOrder);
            Assert.AreEqual(tmdbApi.GetProfileUrl("/actor-raw.jpg")?.ToString(), actorExactZhCn.ImageUrl);

            Assert.AreEqual("這個演員原名", GetPersonByTmdbId(result.People, 1002).Name, "strict path 命中精确 zh-CN detail 时应返回它，即使文本本身是繁体。 ");
            Assert.AreEqual("Actor Hans Raw", GetPersonByTmdbId(result.People, 1003).Name, "strict path 命中精确 zh-CN detail 时应返回它，即使文本本身是拉丁字母。 ");
            Assert.AreEqual("这个演员详情名", GetPersonByTmdbId(result.People, 1004).Name, "精确 zh-CN detail 可用时应直接采用。 ");
            Assert.AreEqual("这个演员翻译名", GetPersonByTmdbId(result.People, 1005).Name, "精确 zh-CN detail 为空时应继续使用精确 zh-CN translation。 ");
            Assert.IsFalse(HasPersonByTmdbId(result.People, 1006), "strict path 在精确 zh-CN detail/translation 都为空时应返回 null，不得回退到 raw。 ");

            var director = GetPersonByTmdbId(result.People, 2001);
            Assert.AreEqual("这个导演大陆名", director.Name, "strict crew path 命中精确 zh-CN detail 时应优先返回它，而不是 raw credits 名称。 ");
            Assert.AreEqual("Director", director.Role);
            Assert.AreEqual(PersonKind.Director, director.Type);
            Assert.AreEqual(tmdbApi.GetPosterUrl("/director.jpg")?.ToString(), director.ImageUrl);

            var producer = GetPersonByTmdbId(result.People, 2002);
            Assert.AreEqual("這個製片人繁體名", producer.Name, "strict crew path 命中精确 zh-CN detail 时应优先返回它，不再保留 raw credits 名称。 ");
            Assert.AreEqual(PersonKind.Producer, producer.Type);
            Assert.AreEqual("Producer", producer.Role);

            var writer = GetPersonByTmdbId(result.People, 2003);
            Assert.AreEqual("这个编剧翻译名", writer.Name, "strict crew path 在精确 zh-CN detail 为空时应仅允许精确 zh-CN translation 作为兜底。 ");
            Assert.AreEqual(PersonKind.Actor, writer.Type, "writer 当前仍沿用既有 PersonKind 映射，不应在本任务里改动。 ");
            Assert.AreEqual("Screenplay", writer.Role);

            Assert.IsFalse(HasPersonByTmdbId(result.People, 2004), "strict crew path 在精确 zh-CN detail/translation 都为空时，不应写入 movie item-level people。 ");
            Assert.IsFalse(result.People.Any(person => person.ProviderIds != null
                && person.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var providerId)
                && string.Equals(providerId, "2999", StringComparison.Ordinal)), "不应改变 crew 过滤规则。 ");
        }

        [TestMethod]
        public async Task GetMetadataByTMDB_StampsPeopleRefreshStateEvenWhenNoPeopleAreAccepted()
        {
            var info = new MovieInfo
            {
                Name = "空人物示例电影",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetaSharkPlugin.ProviderId, "Tmdb_9150" },
                    { MetadataProvider.Tmdb.ToString(), "9150" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var seededMovie = new TmdbMovie
            {
                Id = 9150,
                Title = "空人物示例电影",
                OriginalTitle = "No Accepted People Movie",
                ImdbId = "tt0915000",
                Overview = "TMDb seeded movie overview",
                Tagline = "TMDb seeded movie tagline",
                ReleaseDate = new DateTime(2024, 1, 15),
                VoteAverage = 7.5,
                ProductionCountries = new List<ProductionCountry>(),
                Genres = new List<TmdbGenre>(),
            };
            SetTmdbMovieCredits(
                seededMovie,
                new[]
                {
                    CreateCastCredit(1501, string.Empty, "角色Z", 0, "/rejected-actor.jpg"),
                },
                new[]
                {
                    new Dictionary<string, object?> { ["Id"] = 2501, ["Name"] = string.Empty, ["Department"] = "Production", ["Job"] = "Director", ["ProfilePath"] = "/rejected-director.jpg" },
                    new Dictionary<string, object?> { ["Id"] = 2502, ["Name"] = string.Empty, ["Department"] = "Writing", ["Job"] = "Screenplay", ["ProfilePath"] = "/rejected-writer.jpg" },
                });
            SeedTmdbMovie(tmdbApi, 9150, "zh-CN", seededMovie);

            SeedBlankExactZhCnCastPerson(tmdbApi, 1501);
            SeedBlankExactZhCnCastPerson(tmdbApi, 2501);
            SeedBlankExactZhCnCastPerson(tmdbApi, 2502);

            var provider = new MovieProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.IsTrue(result.People == null || result.People.Count == 0, "当 cast/crew 都没有可接受的 zh-CN 源值时，不应生成任何 people。 ");
            Assert.IsFalse(result.Item.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false, "即使没有 accepted people，provider 也不应把内部 state 写回 provider ids。 ");
        }

        [TestMethod]
        public async Task GetMetadataByTMDB_CastAcceptsLateExactZhCnActorsBeforeApplyingMaxCount()
        {
            var info = new MovieInfo
            {
                Name = "延后演员示例电影",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetaSharkPlugin.ProviderId, "Tmdb_9200" },
                    { MetadataProvider.Tmdb.ToString(), "9200" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var seededMovie = new TmdbMovie
            {
                Id = 9200,
                Title = "延后演员示例电影",
                OriginalTitle = "Late Cast Movie",
                ImdbId = "tt0920000",
                Overview = "TMDb seeded movie overview",
                Tagline = "TMDb seeded movie tagline",
                ReleaseDate = new DateTime(2024, 2, 2),
                VoteAverage = 8.1,
                ProductionCountries = new List<ProductionCountry>(),
                Genres = new List<TmdbGenre>(),
            };

            var castEntries = new List<Dictionary<string, object?>>();
            for (var order = 0; order < 15; order++)
            {
                var tmdbId = 1101 + order;
                castEntries.Add(CreateCastCredit(tmdbId, $"Late Raw Actor {order}", $"前段角色{order}", order, $"/late-rejected-{order}.jpg"));
                SeedBlankExactZhCnCastPerson(tmdbApi, tmdbId);
            }

            var lateExactZhCnNames = new Dictionary<int, string>
            {
                [15] = "这个演员甲",
                [16] = "这个演员乙",
                [17] = "这个演员丙",
                [18] = "这个演员丁",
                [19] = "这个演员戊",
            };

            var expectedLateAcceptedIds = new List<int>();
            for (var order = 15; order < 20; order++)
            {
                var tmdbId = 1101 + order;
                castEntries.Add(CreateCastCredit(tmdbId, $"Late Accepted Raw {order}", $"后段角色{order}", order, $"/late-accepted-{order}.jpg"));
                SeedExactZhCnCastPerson(tmdbApi, tmdbId, lateExactZhCnNames[order]);
                expectedLateAcceptedIds.Add(tmdbId);
            }

            SetTmdbMovieCredits(seededMovie, castEntries, Array.Empty<Dictionary<string, object?>>());
            SeedTmdbMovie(tmdbApi, 9200, "zh-CN", seededMovie);

            var provider = new MovieProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.People);
            Assert.AreEqual(expectedLateAcceptedIds.Count, result.People.Count, "strict cast path 应先按精确 zh-CN 接受演员，再应用上限；只有 raw 的前 15 个演员不应占用名额。 ");
            CollectionAssert.AreEqual(expectedLateAcceptedIds, result.People.Select(GetTmdbProviderId).ToList(), "演员顺序应保持 cast 的原始相对顺序，并在接受后再计入上限。 ");

            for (var order = 0; order < 15; order++)
            {
                Assert.IsFalse(HasPersonByTmdbId(result.People, 1101 + order), "strict cast path 在精确 zh-CN detail/translation 缺失时不应保留 raw-only 演员。 ");
            }

            for (var order = 15; order < 20; order++)
            {
                var tmdbId = 1101 + order;
                var actor = GetPersonByTmdbId(result.People, tmdbId);
                Assert.AreEqual(lateExactZhCnNames[order], actor.Name, "strict cast path 命中精确 zh-CN detail 时应优先返回它，而不是 raw credits 名称。 ");
                Assert.AreEqual($"后段角色{order}", actor.Role);
                Assert.AreEqual(PersonKind.Actor, actor.Type);
                Assert.AreEqual(order, actor.SortOrder);
                Assert.AreEqual(tmdbApi.GetProfileUrl($"/late-accepted-{order}.jpg")?.ToString(), actor.ImageUrl);
            }

            Assert.IsFalse(result.People.Any(person => person.Name.StartsWith("Late Raw Actor", StringComparison.Ordinal)), "strict cast path 不应让 raw-only 名称进入结果。 ");
            Assert.IsFalse(result.People.Any(person => person.Name.StartsWith("Late Accepted Raw", StringComparison.Ordinal)), "strict cast path 命中精确 zh-CN detail 时不应回落到 raw credits 名称。 ");
        }

        [TestMethod]
        public async Task GetMetadataByTMDB_CastWithInsufficientExactZhCnActorsDoesNotCreateFallbacksOrDuplicates()
        {
            var info = new MovieInfo
            {
                Name = "稀疏演员示例电影",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetaSharkPlugin.ProviderId, "Tmdb_9300" },
                    { MetadataProvider.Tmdb.ToString(), "9300" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var seededMovie = new TmdbMovie
            {
                Id = 9300,
                Title = "稀疏演员示例电影",
                OriginalTitle = "Sparse Cast Movie",
                ImdbId = "tt0930000",
                Overview = "TMDb seeded movie overview",
                Tagline = "TMDb seeded movie tagline",
                ReleaseDate = new DateTime(2024, 3, 3),
                VoteAverage = 7.9,
                ProductionCountries = new List<ProductionCountry>(),
                Genres = new List<TmdbGenre>(),
            };

            var acceptedActors = new Dictionary<int, string>
            {
                [1201] = "这个演员己",
                [1204] = "这个演员庚",
                [1216] = "这个演员辛",
                [1219] = "这个演员壬",
            };

            var castEntries = new List<Dictionary<string, object?>>();
            for (var order = 0; order < 20; order++)
            {
                var tmdbId = 1201 + order;
                castEntries.Add(CreateCastCredit(tmdbId, string.Empty, $"稀疏角色{order}", order, $"/sparse-actor-{order}.jpg"));

                if (acceptedActors.TryGetValue(tmdbId, out var exactZhCnName))
                {
                    SeedExactZhCnCastPerson(tmdbApi, tmdbId, exactZhCnName);
                    continue;
                }

                SeedBlankExactZhCnCastPerson(tmdbApi, tmdbId);
            }

            SetTmdbMovieCredits(seededMovie, castEntries, Array.Empty<Dictionary<string, object?>>());
            SeedTmdbMovie(tmdbApi, 9300, "zh-CN", seededMovie);

            var provider = new MovieProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.People);
            Assert.AreEqual(acceptedActors.Count, result.People.Count, "exact zh-CN actor 不足上限时，不应补空位、重复项或 raw fallback。 ");

            var actualActorIds = result.People.Select(GetTmdbProviderId).ToList();
            CollectionAssert.AreEqual(acceptedActors.Keys.ToList(), actualActorIds, "结果应只包含命中精确 zh-CN 的演员，并保持原始顺序。 ");
            Assert.AreEqual(actualActorIds.Distinct().Count(), actualActorIds.Count, "结果中不应出现重复演员。 ");
            Assert.IsFalse(result.People.Any(person => string.IsNullOrWhiteSpace(person.Name)), "结果中不应出现空名字占位项。 ");
            Assert.IsFalse(result.People.Any(person => person.Name.StartsWith("Sparse Raw Actor", StringComparison.Ordinal)), "exact zh-CN actor 不足上限时也不应回退 raw 英文名。 ");

            foreach (var entry in acceptedActors)
            {
                var actor = GetPersonByTmdbId(result.People, entry.Key);
                var order = entry.Key - 1201;
                Assert.AreEqual(entry.Value, actor.Name);
                Assert.AreEqual($"稀疏角色{order}", actor.Role);
                Assert.AreEqual(order, actor.SortOrder);
                Assert.AreEqual(tmdbApi.GetProfileUrl($"/sparse-actor-{order}.jpg")?.ToString(), actor.ImageUrl);
            }

            Assert.IsFalse(HasPersonByTmdbId(result.People, 1202), "未命中可接受精确 zh-CN 的演员不应被补入结果。 ");
            Assert.IsFalse(HasPersonByTmdbId(result.People, 1220), "尾部未命中可接受精确 zh-CN 的演员也不应被补入结果。 ");
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
