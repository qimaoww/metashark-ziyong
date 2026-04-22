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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using TMDbLib.Objects.General;
using TmdbPerson = TMDbLib.Objects.People.Person;
using TMDbLib.Objects.Search;
using TmdbTranslationData = TMDbLib.Objects.General.TranslationData;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class SeriesProviderTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-series-provider-tests");
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


        private static void ConfigureTmdbPosterConfig(TmdbApi tmdbApi)
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

        private static IMemoryCache GetDoubanMemoryCache(DoubanApi doubanApi)
        {
            var memoryCacheField = typeof(DoubanApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "DoubanApi.memoryCache 未定义");

            var memoryCache = memoryCacheField!.GetValue(doubanApi) as IMemoryCache;
            Assert.IsNotNull(memoryCache, "DoubanApi.memoryCache 不是有效的 IMemoryCache");
            return memoryCache!;
        }

        private static void SeedTmdbSeries(TmdbApi tmdbApi, int tmdbId, string language, string name)
        {
            SeedTmdbSeries(
                tmdbApi,
                tmdbId,
                language,
                new TvShow
                {
                    Id = tmdbId,
                    Name = name,
                    OriginalName = name,
                    Overview = "TMDb seeded series overview",
                    FirstAirDate = new DateTime(2011, 10, 4),
                    VoteAverage = 8.8,
                    EpisodeRunTime = new List<int>(),
                    ContentRatings = new ResultContainer<ContentRating>
                    {
                        Results = new List<ContentRating>(),
                    },
                });
        }

        private static void SeedTmdbSeries(TmdbApi tmdbApi, int tmdbId, string language, TvShow series)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"series-{tmdbId}-{language}-{language}",
                series,
                TimeSpan.FromMinutes(5));
        }


        private static void SeedTmdbSeriesSearchResults(TmdbApi tmdbApi, string name, string language, params SearchTv[] results)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"searchseries-{name}-{language}",
                new SearchContainer<SearchTv>
                {
                    Results = results.ToList(),
                },
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

        private static void SetTmdbSeriesCredits(
            TvShow series,
            IEnumerable<Dictionary<string, object?>> castEntries,
            IEnumerable<Dictionary<string, object?>> crewEntries)
        {
            var creditsProperty = typeof(TvShow).GetProperty("Credits");
            Assert.IsNotNull(creditsProperty, "TMDb tv Credits 属性未定义");

            var credits = Activator.CreateInstance(creditsProperty!.PropertyType);
            Assert.IsNotNull(credits, "无法创建 TMDb tv Credits 实例");

            SetTmdbCreditList(credits!, "Cast", castEntries);
            SetTmdbCreditList(credits!, "Crew", crewEntries);
            creditsProperty.SetValue(series, credits);
        }

        private static void SetTmdbSeriesAggregateCredits(
            TvShow series,
            IEnumerable<Dictionary<string, object?>> castEntries,
            IEnumerable<Dictionary<string, object?>> crewEntries)
        {
            var creditsProperty = typeof(TvShow).GetProperty("AggregateCredits");
            Assert.IsNotNull(creditsProperty, "TMDb tv AggregateCredits 属性未定义");

            var credits = Activator.CreateInstance(creditsProperty!.PropertyType);
            Assert.IsNotNull(credits, "无法创建 TMDb tv AggregateCredits 实例");

            SetTmdbCreditList(credits!, "Cast", castEntries);
            SetTmdbCreditList(credits!, "Crew", crewEntries);
            creditsProperty.SetValue(series, credits);
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

        private static Dictionary<string, object?> CreateCastCredit(int id, string name, string character, int order, string? profilePath = null)
        {
            var entry = new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["Name"] = name,
                ["Character"] = character,
                ["Order"] = order,
            };

            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                entry["ProfilePath"] = profilePath;
            }

            return entry;
        }

        private static Dictionary<string, object?> CreateCrewCredit(int id, string name, string department, string job, string? profilePath = null)
        {
            var entry = new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["Name"] = name,
                ["Department"] = department,
                ["Job"] = job,
            };

            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                entry["ProfilePath"] = profilePath;
            }

            return entry;
        }

        private static Dictionary<string, object?> CreateAggregateCastCredit(int id, string name, string character, int order, int totalEpisodeCount, string? profilePath = null)
        {
            var entry = new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["Name"] = name,
                ["Order"] = order,
                ["TotalEpisodeCount"] = totalEpisodeCount,
                ["Roles"] = CreateAggregateCastRoles((character, totalEpisodeCount)),
            };

            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                entry["ProfilePath"] = profilePath;
            }

            return entry;
        }

        private static IList CreateAggregateCastRoles(params (string Character, int EpisodeCount)[] roles)
        {
            var roleType = typeof(TvShow).Assembly.GetType("TMDbLib.Objects.TvShows.CastRole");
            Assert.IsNotNull(roleType, "TMDb CastRole 类型未定义");

            var listType = typeof(List<>).MakeGenericType(roleType!);
            var list = Activator.CreateInstance(listType) as IList;
            Assert.IsNotNull(list, "无法创建 TMDb CastRole 列表实例");

            foreach (var roleData in roles)
            {
                var role = Activator.CreateInstance(roleType!);
                Assert.IsNotNull(role, "无法创建 TMDb CastRole 实例");
                roleType!.GetProperty("Character")!.SetValue(role, roleData.Character);
                roleType!.GetProperty("EpisodeCount")!.SetValue(role, roleData.EpisodeCount);
                list!.Add(role);
            }

            return list!;
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

        private static int GetTmdbId(PersonInfo person)
        {
            Assert.IsNotNull(person.ProviderIds, "人物缺少 provider ids。 ");
            Assert.IsTrue(person.ProviderIds!.TryGetValue(MetadataProvider.Tmdb.ToString(), out var providerId), "人物缺少 TMDb provider id。 ");
            Assert.IsTrue(int.TryParse(providerId, out var tmdbId), $"人物 TMDb provider id 无法解析为整数: {providerId}");
            return tmdbId;
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


        [TestMethod]
        public void TestGetSearchResults_PreservesTmdbResultShape()
        {
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            ConfigureTmdbPosterConfig(tmdbApi);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var searchResult = new SearchTv
            {
                Id = 261780,
                Name = "示例剧集",
                OriginalName = "Example Original",
                PosterPath = "/poster.jpg",
                Overview = "这里是简介",
                FirstAirDate = new DateTime(2020, 4, 10),
            };

            var method = typeof(SeriesProvider).GetMethod("MapTmdbSeriesSearchResult", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(SearchTv) }, null);
            Assert.IsNotNull(method);

            var result = method!.Invoke(provider, new object[] { searchResult }) as RemoteSearchResult;
            Assert.IsNotNull(result);
            Assert.IsNotNull(result!.ProviderIds);
            Assert.AreEqual(2, result.ProviderIds.Count);
            Assert.AreEqual("261780", result.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual("Tmdb_261780", result.ProviderIds[MetaSharkPlugin.ProviderId]);
            Assert.AreEqual("[TMDB]示例剧集", result.Name);
            Assert.AreEqual(tmdbApi.GetPosterUrl(searchResult.PosterPath)?.ToString(), result.ImageUrl);
            Assert.AreEqual(searchResult.Overview, result.Overview);
            Assert.AreEqual(2020, result.ProductionYear);
        }

        [TestMethod]
        public void TestGetMetadata()
        {
            var info = new SeriesInfo() { Name = "天下长河" };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetMetadata(info, CancellationToken.None);
                Assert.IsNotNull(result);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetAnimeMetadata()
        {
            var info = new SeriesInfo() { Name = "命运-冠位嘉年华" };
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
                    var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);
                    Assert.IsNotNull(result.Item);
                    Assert.IsTrue(result.HasMetadata);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(result.Item.Name));

                    if (string.IsNullOrEmpty(result.Item.GetProviderId(MetadataProvider.Tmdb)))
                    {
                        Assert.AreEqual("命运/冠位指定嘉年华 公元2020奥林匹亚英灵限界大祭", result.Item.Name);
                        Assert.AreEqual("Fate/Grand Carnival", result.Item.OriginalTitle);
                    }

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
        public void TestGetMetadataFallsBackToTmdbWhenDoubanBlocked()
        {
            var info = new SeriesInfo()
            {
                Name = "花牌情缘",
                MetadataLanguage = "zh",
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
            SeedTmdbSeries(tmdbApi, 45247, "zh", "花牌情缘");
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);
                    Assert.IsNotNull(result.Item);
                    Assert.AreEqual("45247", result.Item.GetProviderId(MetadataProvider.Tmdb));
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
        public void TestGetSearchResults_ReturnsTmdbMatchWhenProviderIdPresentAndNameMissing()
        {
            var info = new SeriesInfo()
            {
                Name = null,
                MetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), "261780" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            ConfigureTmdbPosterConfig(tmdbApi);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var results = await provider.GetSearchResults(info, CancellationToken.None);
                    Assert.IsNotNull(results);
                    var resultList = results.ToList();
                    Assert.IsTrue(resultList.Count >= 1, "Expected at least one result when TMDb provider id is present");

                    var tmdbResult = resultList.FirstOrDefault(r => r.ProviderIds.ContainsKey(MetadataProvider.Tmdb.ToString()));
                    Assert.IsNotNull(tmdbResult, "Expected a result with TMDb provider id");
                    Assert.AreEqual("261780", tmdbResult.ProviderIds[MetadataProvider.Tmdb.ToString()]);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests
                    || ex.Message.Contains("429", StringComparison.Ordinal))
                {
                    Assert.Inconclusive("TMDb rate limited (429)." + ex.Message);
                }
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetSearchResults_UsesExplicitTmdbIdEvenWhenTmdbSearchListingDisabled()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);
            const int tmdbId = 261780;
            const string metadataLanguage = "zh";

            var originalEnableTmdbSearch = plugin.Configuration.EnableTmdbSearch;
            try
            {
                plugin.Configuration.EnableTmdbSearch = false;
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
                var doubanApi = DoubanApiTestHelper.CreateBlockedDoubanApi(loggerFactory);
                var tmdbApi = new TmdbApi(loggerFactory);
                ConfigureTmdbPosterConfig(tmdbApi);
                var omdbApi = new OmdbApi(loggerFactory);
                var imdbApi = new ImdbApi(loggerFactory);

                Task.Run(async () =>
                {
                    try
                    {
                        var tvShow = await tmdbApi.GetSeriesAsync(tmdbId, metadataLanguage, metadataLanguage, CancellationToken.None);
                        if (tvShow == null)
                        {
                            Assert.Inconclusive("TMDb series 261780 was not available for the toggle test.");
                            return;
                        }

                        var searchName = tvShow.Name ?? tvShow.OriginalName;
                        if (string.IsNullOrWhiteSpace(searchName))
                        {
                            Assert.Inconclusive("TMDb series 261780 returned no usable title for the toggle test.");
                            return;
                        }

                        var info = new SeriesInfo()
                        {
                            Name = searchName,
                            MetadataLanguage = metadataLanguage,
                            ProviderIds = new Dictionary<string, string>
                            {
                                { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                            },
                        };

                        var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var results = await provider.GetSearchResults(info, CancellationToken.None);
                        Assert.IsNotNull(results);
                        var resultList = results.ToList();
                        Assert.IsTrue(resultList.Count >= 1, "Expected at least one result even when EnableTmdbSearch is false and a title is present.");

                        var tmdbResult = resultList.FirstOrDefault(r => r.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var id) && id == tmdbId.ToString());
                        Assert.IsNotNull(tmdbResult, "Expected explicit TMDb ID fast path to still return the TMDb match when EnableTmdbSearch is false and a title is present.");
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests
                        || ex.Message.Contains("429", StringComparison.Ordinal))
                    {
                        Assert.Inconclusive("TMDb rate limited (429)." + ex.Message);
                    }
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.EnableTmdbSearch = originalEnableTmdbSearch;
            }
        }

        [TestMethod]
        public void TestGetSearchResults_FallsBackWhenTmdbIdInvalid()
        {
            EnsurePluginInstance();
            var originalEnableTmdbSearch = MetaSharkPlugin.Instance?.Configuration.EnableTmdbSearch;
            try
            {
                if (MetaSharkPlugin.Instance != null)
                {
                    MetaSharkPlugin.Instance.Configuration.EnableTmdbSearch = false;
                }

                var info = new SeriesInfo()
                {
                    Name = "老友记",
                    MetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), "invalid-id" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
                var doubanApi = DoubanApiTestHelper.CreateBlockedDoubanApi(loggerFactory);
                DoubanApiTestHelper.SeedTvSearchResult(doubanApi, info.Name!, "1291841", "老友记", 1994);
                var tmdbApi = new TmdbApi(loggerFactory);
                ConfigureTmdbPosterConfig(tmdbApi);
                var omdbApi = new OmdbApi(loggerFactory);
                var imdbApi = new ImdbApi(loggerFactory);

                Task.Run(async () =>
                {
                    try
                    {
                        var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var results = await provider.GetSearchResults(info, CancellationToken.None);
                        Assert.IsNotNull(results);
                        var resultList = results.ToList();

                        Assert.IsTrue(resultList.Any(), "Expected invalid TMDb provider id to continue into title search and return at least one result.");
                        Assert.IsTrue(
                            resultList.Any(r => r.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId)),
                            "Expected invalid TMDb provider id to fall back to the existing Douban title-search path when TMDb title search is disabled.");
                        Assert.IsTrue(
                            resultList.Any(r => r.ProviderIds.TryGetValue(BaseProvider.DoubanProviderId, out var id) && id == "1291841"),
                            "Expected the fallback result to come from the seeded Douban title-search cache entry rather than live network behavior.");
                        Assert.IsFalse(
                            resultList.Any(r => r.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var id) && id == "invalid-id"),
                            "Invalid explicit TMDb provider id should not be emitted as a search result.");
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests
                        || ex.Message.Contains("429", StringComparison.Ordinal))
                    {
                        Assert.Inconclusive("Douban rate limited (429)." + ex.Message);
                    }
                }).GetAwaiter().GetResult();
            }
            finally
            {
                if (MetaSharkPlugin.Instance != null && originalEnableTmdbSearch.HasValue)
                {
                    MetaSharkPlugin.Instance.Configuration.EnableTmdbSearch = originalEnableTmdbSearch.Value;
                }
            }
        }

        [TestMethod]
        public void TestGetSearchResults_ReturnsEmptyWhenTmdbIdInvalidAndNameMissing()
        {
            var info = new SeriesInfo()
            {
                Name = null,
                MetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), "not-a-number" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            ConfigureTmdbPosterConfig(tmdbApi);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var results = await provider.GetSearchResults(info, CancellationToken.None);
                    Assert.IsNotNull(results);
                    var resultList = results.ToList();
                    Assert.AreEqual(0, resultList.Count, "Should return empty results when TMDb ID is invalid and no title provided");
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests
                    || ex.Message.Contains("429", StringComparison.Ordinal))
                {
                    Assert.Inconclusive("Rate limited (429)." + ex.Message);
                }
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetSearchResults_ReturnsEmptyWhenTmdbIdNotFoundAndNameMissing()
        {
            const int tmdbId = 123456789;
            const string metadataLanguage = "zh";

            var info = new SeriesInfo()
            {
                Name = null,
                MetadataLanguage = metadataLanguage,
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);

            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField);

            var memoryCache = memoryCacheField!.GetValue(tmdbApi) as IMemoryCache;
            Assert.IsNotNull(memoryCache);

            memoryCache!.Set($"series-{tmdbId}-{metadataLanguage}-{metadataLanguage}", (TvShow?)null, TimeSpan.FromMinutes(1));

            Task.Run(async () =>
            {
                var cachedLookup = await tmdbApi.GetSeriesAsync(tmdbId, metadataLanguage, metadataLanguage, CancellationToken.None);
                Assert.IsNull(cachedLookup, "Expected the seeded TMDb lookup to simulate a not-found response.");

                var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var results = await provider.GetSearchResults(info, CancellationToken.None);
                Assert.IsNotNull(results);

                var resultList = results.ToList();
                Assert.AreEqual(0, resultList.Count, "Should return empty results when explicit positive TMDb ID resolves to no series and no title is available.");
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetSearchResults_SkipsTmdbTitleSearchAfterExactTmdbHit()
        {
            EnsurePluginInstance();
            var originalEnableTmdbSearch = MetaSharkPlugin.Instance?.Configuration.EnableTmdbSearch;
            try
            {
                if (MetaSharkPlugin.Instance != null)
                {
                    MetaSharkPlugin.Instance.Configuration.EnableTmdbSearch = true;
                }

                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
                var doubanApi = new DoubanApi(loggerFactory);
                var tmdbApi = new TmdbApi(loggerFactory);
                ConfigureTmdbPosterConfig(tmdbApi);
                var omdbApi = new OmdbApi(loggerFactory);
                var imdbApi = new ImdbApi(loggerFactory);

                Task.Run(async () =>
                {
                    try
                    {
                        var tvShow = await tmdbApi.GetSeriesAsync(261780, "zh", "zh", CancellationToken.None);
                        if (tvShow == null)
                        {
                            Assert.Inconclusive("TMDb series 261780 was not available for the duplicate-suppression test.");
                            return;
                        }

                        var searchName = tvShow.Name ?? tvShow.OriginalName;
                        if (string.IsNullOrWhiteSpace(searchName))
                        {
                            Assert.Inconclusive("TMDb series 261780 returned no usable title for the duplicate-suppression test.");
                            return;
                        }

                        var titleResults = await tmdbApi.SearchSeriesAsync(searchName, "zh", CancellationToken.None);
                        if (!titleResults.Any(x => x.Id == 261780))
                        {
                            Assert.Inconclusive("TMDb title search did not return the explicit series id in the current environment.");
                            return;
                        }

                        var info = new SeriesInfo()
                        {
                            Name = searchName,
                            MetadataLanguage = "zh",
                            ProviderIds = new Dictionary<string, string>
                            {
                                { MetadataProvider.Tmdb.ToString(), "261780" },
                            },
                        };

                        var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var results = (await provider.GetSearchResults(info, CancellationToken.None)).ToList();
                        Assert.AreEqual(1, results.Count(r => r.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var id) && id == "261780"));
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests
                        || ex.Message.Contains("429", StringComparison.Ordinal))
                    {
                        Assert.Inconclusive("TMDb rate limited (429)." + ex.Message);
                    }
                }).GetAwaiter().GetResult();
            }
            finally
            {
                if (MetaSharkPlugin.Instance != null && originalEnableTmdbSearch.HasValue)
                {
                    MetaSharkPlugin.Instance.Configuration.EnableTmdbSearch = originalEnableTmdbSearch.Value;
                }
            }
        }

        [TestMethod]
        public void TestGetSearchResults_TitleOnlyBehaviorUnchanged()
        {
            EnsurePluginInstance();
            var originalEnableTmdbSearch = MetaSharkPlugin.Instance?.Configuration.EnableTmdbSearch;
            try
            {
                if (MetaSharkPlugin.Instance != null)
                {
                    MetaSharkPlugin.Instance.Configuration.EnableTmdbSearch = true;
                }

                var info = new SeriesInfo()
                {
                    Name = "老友记",
                    MetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>(),
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
                var doubanApi = new DoubanApi(loggerFactory);
                var tmdbApi = new TmdbApi(loggerFactory);
                ConfigureTmdbPosterConfig(tmdbApi);
                var omdbApi = new OmdbApi(loggerFactory);
                var imdbApi = new ImdbApi(loggerFactory);

                Task.Run(async () =>
                {
                    try
                    {
                        var titleResults = await tmdbApi.SearchSeriesAsync(info.Name, info.MetadataLanguage, CancellationToken.None);
                        if (!titleResults.Any())
                        {
                            Assert.Inconclusive("TMDb title search returned no results in the current environment.");
                            return;
                        }

                        var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var results = (await provider.GetSearchResults(info, CancellationToken.None)).ToList();

                        Assert.IsTrue(
                            results.Any(r => r.ProviderIds.ContainsKey(MetadataProvider.Tmdb.ToString())),
                            "Expected TMDb title-search results to remain available when no explicit TMDb provider id is supplied.");
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests
                        || ex.Message.Contains("429", StringComparison.Ordinal))
                    {
                        Assert.Inconclusive("Douban rate limited (429)." + ex.Message);
                    }
                }).GetAwaiter().GetResult();
            }
            finally
            {
                if (MetaSharkPlugin.Instance != null && originalEnableTmdbSearch.HasValue)
                {
                    MetaSharkPlugin.Instance.Configuration.EnableTmdbSearch = originalEnableTmdbSearch.Value;
                }
            }
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
                Assert.AreEqual(PluginConfiguration.DefaultScraperModeTmdbOnly, plugin.Configuration.DefaultScraperMode);
                Assert.IsFalse(Jellyfin.Plugin.MetaShark.Providers.DefaultScraperPolicy.IsDoubanAllowed(plugin.Configuration, DefaultScraperSemantic.AutomaticRefresh));

                var info = new SeriesInfo()
                {
                    Name = "花牌情缘",
                    MetadataLanguage = "zh",
                    IsAutomated = true,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "6439459" },
                        { MetadataProvider.Tmdb.ToString(), "45247" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = null,
                };
                var doubanApi = CreateThrowingDoubanApi(loggerFactory, "tmdb-only 自动剧集元数据链路不应再访问 Douban。");
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbSeries(tmdbApi, 45247, "zh", "花牌情缘");
                var omdbApi = new OmdbApi(loggerFactory);
                var imdbApi = new ImdbApi(loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsNotNull(result.Item, "tmdb-only 自动剧集链路在存在有效 TMDb 路由时应改道到 TMDb。");
                    Assert.IsTrue(result.HasMetadata);
                    Assert.AreEqual("45247", result.Item.GetProviderId(MetadataProvider.Tmdb));
                    Assert.AreEqual("花牌情缘", result.Item.Name);
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

                var info = new SeriesInfo()
                {
                    Name = "花牌情缘",
                    Path = "/test/[douban-6439459] 花牌情缘 S01E01.mkv",
                    MetadataLanguage = "zh",
                    IsAutomated = true,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), "45247" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = null,
                };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动剧集元数据链路不应因文件名里的 douban hint 回落到 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                SeedTmdbSeries(tmdbApi, 45247, "zh", "花牌情缘");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsNotNull(result.Item, "tmdb-only 自动剧集链路在存在有效 TMDb 路由时应继续改道到 TMDb。 ");
                    Assert.IsTrue(result.HasMetadata);
                    Assert.AreEqual("45247", result.Item.GetProviderId(MetadataProvider.Tmdb));
                    Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId), "文件名中的 Douban hint 不应被视为 tmdb-only 自动链路的豁免条件。");
                    Assert.AreEqual("花牌情缘", result.Item.Name);
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

                var info = new SeriesInfo()
                {
                    Name = "老友记",
                    MetadataLanguage = "zh",
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = null,
                };
                var doubanApi = new DoubanApi(loggerFactory);
                DoubanApiTestHelper.SeedTvSearchResult(doubanApi, info.Name!, "1291841", "老友记", 1994);
                var tmdbApi = new TmdbApi(loggerFactory);
                var omdbApi = new OmdbApi(loggerFactory);
                var imdbApi = new ImdbApi(loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var results = (await provider.GetSearchResults(info, CancellationToken.None)).ToList();

                    Assert.IsTrue(results.Any(r => r.ProviderIds.TryGetValue(BaseProvider.DoubanProviderId, out var sid) && sid == "1291841"), "tmdb-only 不应误伤手动 Identify 的 Douban 搜索结果。");
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

                var info = new SeriesInfo
                {
                    Name = "老友记",
                    MetadataLanguage = "zh",
                    IsAutomated = true,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "1291841" },
                        { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_1291841" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = CreateManualMatchContextAccessor();
                var doubanApi = new DoubanApi(loggerFactory);
                SeedDoubanSubject(
                    doubanApi,
                    new DoubanSubject
                    {
                        Sid = "1291841",
                        Name = "老友记",
                        OriginalName = "Friends",
                        Year = 1994,
                        Category = "电视剧",
                        Genre = "喜剧 / 爱情",
                        Intro = "豆瓣手动匹配详情",
                        Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000000.webp",
                        Screen = "1994-09-22",
                    });
                var tmdbApi = new TmdbApi(loggerFactory);
                var omdbApi = new OmdbApi(loggerFactory);
                var imdbApi = new ImdbApi(loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetMetadata(info, CancellationToken.None);

                    Assert.IsNotNull(result.Item, "显式手动匹配语义下仍应允许使用 Douban 剧集详情。");
                    Assert.IsTrue(result.HasMetadata);
                    Assert.AreEqual("1291841", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
                    Assert.AreEqual("老友记", result.Item.Name);
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
        public async Task GetMetadataByDouban_AllowsEmptyItemLevelPeople_WhenTmdbIdIsUnavailable()
        {
            var info = new SeriesInfo
            {
                Name = "豆瓣示例剧集",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "series-douban-100" },
                    { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_series-douban-100" },
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
                    Sid = "series-douban-100",
                    Name = "豆瓣示例剧集",
                    OriginalName = "Douban Sample Series",
                    Year = 0,
                    Rating = 8.1f,
                    Genre = "剧情 / 动画",
                    Intro = "豆瓣剧集简介",
                    Screen = "2024-04-04",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000003.webp",
                });
            SeedDoubanCelebrities(
                doubanApi,
                "series-douban-100",
                new[]
                {
                    new DoubanCelebrity
                    {
                        Id = "douban-series-director-1",
                        Name = "豆瓣剧集导演",
                        Role = "导演",
                        Img = "https://img9.doubanio.com/view/celebrity/raw/public/p0000000004.webp",
                    },
                    new DoubanCelebrity
                    {
                        Id = "douban-series-actor-1",
                        Name = "豆瓣剧集演员",
                        Role = "演员",
                        Img = "https://img9.doubanio.com/view/celebrity/raw/public/p0000000005.webp",
                    },
                });
            var tmdbApi = new TmdbApi(this.loggerFactory);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var provider = new SeriesProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("豆瓣示例剧集", result.Item.Name);
            Assert.AreEqual("Douban Sample Series", result.Item.OriginalTitle);
            Assert.AreEqual("豆瓣剧集简介", result.Item.Overview);
            Assert.IsTrue(result.Item.CommunityRating > 8.0f && result.Item.CommunityRating < 8.2f, "Douban 路径仍应保留评分等非 people 元数据。 ");
            Assert.IsNull(result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsTrue(result.People == null || result.People.Count == 0, "拿不到 tmdbId 时，series item-level people 允许为空，但不应再把 Douban celebrities 写进去。 ");
            Assert.IsFalse(result.People?.Any(person => person.ProviderIds != null
                && person.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId)) ?? false, "Douban 元数据路径不应再写入带 Douban provider id 的 series item-level people。 ");
        }

        [TestMethod]
        public async Task GetMetadataByDouban_AddsTmdbOnlyPeople_WhenTmdbIdIsResolved()
        {
            var info = new SeriesInfo
            {
                Name = "豆瓣示例剧集",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "series-douban-101" },
                    { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_series-douban-101" },
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
                    Sid = "series-douban-101",
                    Name = "豆瓣示例剧集",
                    OriginalName = "Douban Sample Series",
                    Year = 2024,
                    Rating = 8.1f,
                    Genre = "剧情 / 动画",
                    Intro = "豆瓣剧集简介",
                    Screen = "2024-04-04",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000006.webp",
                });
            SeedDoubanCelebrities(
                doubanApi,
                "series-douban-101",
                new[]
                {
                    new DoubanCelebrity
                    {
                        Id = "douban-series-director-2",
                        Name = "豆瓣剧集导演",
                        Role = "导演",
                    },
                    new DoubanCelebrity
                    {
                        Id = "douban-series-actor-2",
                        Name = "豆瓣剧集演员",
                        Role = "演员",
                    },
                });
            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedTmdbSeriesSearchResults(
                tmdbApi,
                "豆瓣示例剧集",
                "zh-CN",
                new SearchTv
                {
                    Id = 9102,
                    Name = "豆瓣示例剧集",
                    OriginalName = "Douban Sample Series",
                    FirstAirDate = new DateTime(2024, 1, 1),
                });
            var tmdbSeries = new TvShow
            {
                Id = 9102,
                Name = "TMDb 不应覆盖 Douban 主标题",
                OriginalName = "TMDb Ignored Original Title",
                Overview = "TMDb 不应覆盖 Douban 简介",
                FirstAirDate = new DateTime(2024, 1, 1),
                VoteAverage = 7.1,
                EpisodeRunTime = new List<int>(),
                ContentRatings = new ResultContainer<ContentRating>
                {
                    Results = new List<ContentRating>(),
                },
            };
            SetTmdbSeriesCredits(
                tmdbSeries,
                new[]
                {
                    CreateCastCredit(3101, "TMDb Raw Actor", "角色A", 0),
                },
                new[]
                {
                    CreateCrewCredit(3201, "TMDb Raw Director", "Production", "Director"),
                });
            SeedTmdbSeries(tmdbApi, 9102, "zh-CN", tmdbSeries);
            SeedTmdbPerson(tmdbApi, 3101, "剧集演员中文名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 3201, "剧集导演中文名", language: "zh-CN");
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var provider = new SeriesProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("豆瓣示例剧集", result.Item.Name, "Douban 主字段不应被 TMDb people 回填覆盖。 ");
            Assert.AreEqual("Douban Sample Series", result.Item.OriginalTitle);
            Assert.AreEqual("豆瓣剧集简介", result.Item.Overview);
            Assert.IsTrue(result.Item.CommunityRating > 8.0f && result.Item.CommunityRating < 8.2f, "Douban 主分支的评分不应被 TMDb people 回填改变。 ");
            Assert.AreEqual("9102", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNotNull(result.People);
            Assert.AreEqual(1, result.People.Count, "Douban 路径一旦解析出 tmdbId，应额外复用 TMDb 剧集演员结果。 ");
            Assert.AreEqual("剧集演员中文名", GetPersonByTmdbId(result.People, 3101).Name);
            Assert.IsFalse(result.People.Any(person => GetTmdbId(person) == 3201), "剧集人物结果不应再混入 crew。 ");
            Assert.IsTrue(result.People.All(person => person.ProviderIds != null
                && person.ProviderIds.ContainsKey(MetadataProvider.Tmdb.ToString())
                && !person.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId)), "Douban 分支补回的人物仍应保持 TMDb-only provider id。 ");
        }

        [TestMethod]
        public async Task GetMetadataByTMDB_ScrapesOnlySeriesPeopleWithExactZhCnSourceNames()
        {
            var expectedAcceptedActorIds = new[] { 1001, 1002, 1003, 1004, 1005, 1011, 1012, 1013, 1014, 1015, 1016, 1017, 1018, 1019, 1020 };
            var info = new SeriesInfo
            {
                Name = "示例剧集",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetaSharkPlugin.ProviderId, "Tmdb_9101" },
                    { MetadataProvider.Tmdb.ToString(), "9101" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbPosterConfig(tmdbApi);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var seededSeries = new TvShow
            {
                Id = 9101,
                Name = "示例剧集",
                OriginalName = "Sample Series",
                Overview = "TMDb seeded series overview",
                FirstAirDate = new DateTime(2024, 1, 1),
                VoteAverage = 8.4,
                EpisodeRunTime = new List<int>(),
                ContentRatings = new ResultContainer<ContentRating>
                {
                    Results = new List<ContentRating>(),
                },
            };
            var castEntries = new List<Dictionary<string, object?>>
            {
                CreateCastCredit(1001, "这个演员原名", "角色A", 0, "/actor-raw.jpg"),
                CreateCastCredit(1002, "這個演員原名", "角色B", 1, "/actor-zh-cn.jpg"),
                CreateCastCredit(1003, "Actor Hans Raw", "角色C", 2, "/actor-zh-hans.jpg"),
                CreateCastCredit(1004, "", "角色D", 3),
                CreateCastCredit(1005, "", "角色E", 4),
            };

            for (var tmdbId = 1006; tmdbId <= 1010; tmdbId++)
            {
                castEntries.Add(CreateCastCredit(tmdbId, string.Empty, $"角色{tmdbId}", tmdbId - 1, $"/actor-rejected-{tmdbId}.jpg"));
            }

            castEntries.Add(CreateCastCredit(1011, "Late Actor Raw 11", "角色K", 10, "/actor-accepted-11.jpg"));
            castEntries.Add(CreateCastCredit(1012, "Late 演員 Raw 12", "角色L", 11, "/actor-accepted-12.jpg"));
            castEntries.Add(CreateCastCredit(1013, "Late Actor Latin 13", "角色M", 12, "/actor-accepted-13.jpg"));
            castEntries.Add(CreateCastCredit(1014, "Late 演員 Raw 14", "角色N", 13, "/actor-accepted-14.jpg"));
            castEntries.Add(CreateCastCredit(1015, "Late Actor Raw 15", "角色O", 14, "/actor-accepted-15.jpg"));
            castEntries.Add(CreateCastCredit(1016, "Late Actor Raw 16", "角色P", 15, "/actor-accepted-16.jpg"));
            castEntries.Add(CreateCastCredit(1017, "Late Actor Raw 17", "角色Q", 16, "/actor-accepted-17.jpg"));
            castEntries.Add(CreateCastCredit(1018, "Late Actor Raw 18", "角色R", 17));
            castEntries.Add(CreateCastCredit(1019, "Late Actor Raw 19", "角色S", 18, "/actor-accepted-19.jpg"));
            castEntries.Add(CreateCastCredit(1020, "这个演员尾部原名", "角色T", 19, "/actor-accepted-20.jpg"));

            SetTmdbSeriesCredits(
                seededSeries,
                castEntries,
                new[]
                {
                    CreateCrewCredit(2001, "這個導演原名", "Production", "Director", "/director.jpg"),
                    CreateCrewCredit(2002, "Producer Raw", "Production", "Producer", "/producer.jpg"),
                    CreateCrewCredit(2003, "Writer Raw", "Writing", "Screenplay", "/writer.jpg"),
                    CreateCrewCredit(2004, string.Empty, "Production", "Director", "/fallback-director.jpg"),
                    CreateCrewCredit(2999, "Ignored Crew", "Art", "Art Direction"),
                });
            SeedTmdbSeries(tmdbApi, 9101, "zh-CN", seededSeries);

            SeedTmdbPerson(tmdbApi, 1001, "这个演员中文候选", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1002, "这个演员中文名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1003, "这个演员 Hans 名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1004, "这个演员详情中文名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1004, "這個演員繁體名", language: "zh-Hans");
            SeedTmdbPerson(tmdbApi, 1004, "这个演员通用名", language: "zh");
            SeedTmdbPersonTranslations(tmdbApi, 1004, CreatePersonTranslation("zh", "CN", "这个演员详情翻译名"));
            SeedTmdbPerson(tmdbApi, 1005, string.Empty, language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1005, "這個演員繁體名", language: "zh-Hans");
            SeedTmdbPerson(tmdbApi, 1005, "这个演员通用名", language: "zh");
            SeedTmdbPersonTranslations(tmdbApi, 1005, CreatePersonTranslation("zh", "CN", "这个演员翻译名"));

            for (var tmdbId = 1006; tmdbId <= 1010; tmdbId++)
            {
                SeedTmdbPerson(tmdbApi, tmdbId, string.Empty, language: "zh-CN");
                SeedTmdbPerson(tmdbApi, tmdbId, $"這個演員{tmdbId}繁體名", language: "zh-Hans");
                SeedTmdbPerson(tmdbApi, tmdbId, $"Actor {tmdbId} Zh", language: "zh");
                SeedTmdbPersonTranslations(tmdbApi, tmdbId, CreatePersonTranslation("zh", "TW", $"這個翻譯演員{tmdbId}"));
            }

            SeedTmdbPerson(tmdbApi, 1011, "这个演员尾部中文名 11", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1012, "安德斯·托马斯·詹森", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1013, "Latin Actor 13", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1014, "这个演员尾部中文名 14", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1015, "这個演員尾部原名 15", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1016, "Late Actor Raw 16 Detail", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1017, "Late Actor Raw 17 Detail", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1018, "Late Actor Raw 18 Detail", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1019, "Late Actor Raw 19 Detail", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1020, "这个演员尾部原名 20", language: "zh-CN");
            SeedTmdbPersonTranslations(tmdbApi, 1016, CreatePersonTranslation("zh", "CN", "这个演员尾部翻译名 16"));
            SeedTmdbPersonTranslations(tmdbApi, 1017, CreatePersonTranslation("zh", "CN", "这个演员尾部翻译名 17"));
            SeedTmdbPersonTranslations(tmdbApi, 1018, CreatePersonTranslation("zh", "CN", "这个演员尾部翻译名 18"));
            SeedTmdbPersonTranslations(tmdbApi, 1019, CreatePersonTranslation("zh", "CN", "这个演员尾部翻译名 19"));
            SeedTmdbPersonTranslations(tmdbApi, 1020, CreatePersonTranslation("zh", "CN", "这个演员尾部翻译名 20"));

            SeedTmdbPerson(tmdbApi, 2001, "这个导演中文名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 2002, "這個製片人繁體名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 2002, "这个制片人Hans名", language: "zh-Hans");
            SeedTmdbPerson(tmdbApi, 2003, string.Empty, language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 2003, " ", language: "zh-Hans");
            SeedTmdbPerson(tmdbApi, 2003, " ", language: "zh");
            SeedTmdbPersonTranslations(tmdbApi, 2003, CreatePersonTranslation("zh", "CN", "这个编剧翻译名"));
            SeedTmdbPerson(tmdbApi, 2004, string.Empty, language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 2004, "這個導演繁體名", language: "zh-Hans");
            SeedTmdbPerson(tmdbApi, 2004, "這個導演繁體名", language: "zh");
            SeedTmdbPersonTranslations(tmdbApi, 2004, CreatePersonTranslation("zh", "CN", string.Empty), CreatePersonTranslation("zh", "TW", "這個翻譯導演"));

            var provider = new SeriesProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.People);
            Assert.IsFalse(result.Item.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false, "provider 不应在返回 metadata result 时提前写入内部 people refresh state。 ");
            Assert.AreEqual(15, result.People.Count, "前 15 个命中精确 zh-CN 源值的演员应按原始顺序进入结果，后续条目即使也可接受也不应挤占上限。 ");

            var actorTmdbIdsInOrder = result.People
                .Where(person =>
                {
                    var tmdbId = GetTmdbId(person);
                    return tmdbId >= 1001 && tmdbId <= 1020;
                })
                .Select(GetTmdbId)
                .ToArray();
            CollectionAssert.AreEqual(expectedAcceptedActorIds, actorTmdbIdsInOrder, "actor 应保持原始相对顺序，并在接受后再计入上限；strict path 应优先使用精确 zh-CN detail/translation，而不是 raw credits 名称。 ");
            Assert.AreEqual(expectedAcceptedActorIds.Length, actorTmdbIdsInOrder.Distinct().Count(), "accepted actor 总数不足 15 时不应补重复项。 ");
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 }, result.People.Where(person =>
            {
                var tmdbId = GetTmdbId(person);
                return tmdbId >= 1001 && tmdbId <= 1020;
            }).Select(person => person.SortOrder).ToArray(), "accept-then-cap 不应重排或重写原始 SortOrder。 ");
            Assert.IsFalse(result.People.Any(person => string.IsNullOrWhiteSpace(person.Name)), "accepted actor 数不足上限时不应制造空位项。 ");

            var actorExactZhCn = GetPersonByTmdbId(result.People, 1001);
            Assert.AreEqual("这个演员中文候选", actorExactZhCn.Name, "strict actor path 命中精确 zh-CN detail 时应优先返回它，而不是 raw credits 名称。 ");
            Assert.AreEqual("角色A", actorExactZhCn.Role);
            Assert.AreEqual(PersonKind.Actor, actorExactZhCn.Type);
            Assert.AreEqual(0, actorExactZhCn.SortOrder);
            Assert.AreEqual(tmdbApi.GetProfileUrl("/actor-raw.jpg")?.ToString(), actorExactZhCn.ImageUrl);

            Assert.AreEqual("这个演员中文名", GetPersonByTmdbId(result.People, 1002).Name, "strict actor path 命中精确 zh-CN detail 时应优先返回它，而不是 raw credits 名称。 ");
            Assert.AreEqual("这个演员 Hans 名", GetPersonByTmdbId(result.People, 1003).Name, "strict actor path 命中精确 zh-CN detail 时应优先返回它，而不是 raw credits 名称。 ");
            Assert.AreEqual("这个演员详情中文名", GetPersonByTmdbId(result.People, 1004).Name, "精确 zh-CN detail 可用时应直接采用。 ");
            Assert.AreEqual("这个演员翻译名", GetPersonByTmdbId(result.People, 1005).Name, "精确 zh-CN detail 为空时应仅使用精确 zh-CN translation。 ");
            Assert.IsFalse(result.People.Any(person => person.ProviderIds != null
                && person.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var providerId)
                && expectedAcceptedActorIds.All(acceptedId => !string.Equals(providerId, acceptedId.ToString(), StringComparison.Ordinal))
                && int.TryParse(providerId, out var tmdbId)
                && tmdbId >= 1006
                && tmdbId <= 1010), "raw 为空且 zh-CN 详情/翻译都为空时才应拒绝，不能因为繁体/Hans/通用 zh 有值就保留。 ");

            Assert.AreEqual("这个演员尾部中文名 11", GetPersonByTmdbId(result.People, 1011).Name, "strict actor path 命中精确 zh-CN detail 时应优先返回它。 ");
            Assert.AreEqual("安德斯·托马斯·詹森", GetPersonByTmdbId(result.People, 1012).Name, "strict actor path 命中精确 zh-CN detail 时应优先返回它。 ");
            Assert.AreEqual("Latin Actor 13", GetPersonByTmdbId(result.People, 1013).Name, "strict actor path 命中精确 zh-CN detail 时应优先返回它。 ");
            Assert.AreEqual("这个演员尾部中文名 14", GetPersonByTmdbId(result.People, 1014).Name, "strict actor path 命中精确 zh-CN detail 时应优先返回它。 ");
            Assert.AreEqual("这個演員尾部原名 15", GetPersonByTmdbId(result.People, 1015).Name, "strict actor path 命中精确 zh-CN detail 时应优先返回它。 ");
            Assert.AreEqual("Late Actor Raw 16 Detail", GetPersonByTmdbId(result.People, 1016).Name, "strict actor path 命中精确 zh-CN detail 时不应回落到 raw。 ");
            Assert.AreEqual("Late Actor Raw 17 Detail", GetPersonByTmdbId(result.People, 1017).Name, "strict actor path 命中精确 zh-CN detail 时不应回落到 raw。 ");
            Assert.AreEqual("Late Actor Raw 18 Detail", GetPersonByTmdbId(result.People, 1018).Name, "strict actor path 命中精确 zh-CN detail 时不应回落到 raw。 ");
            Assert.AreEqual("Late Actor Raw 19 Detail", GetPersonByTmdbId(result.People, 1019).Name, "strict actor path 命中精确 zh-CN detail 时不应回落到 raw。 ");
            Assert.AreEqual("这个演员尾部原名 20", GetPersonByTmdbId(result.People, 1020).Name, "strict actor path 命中精确 zh-CN detail 时应优先返回它。 ");

            Assert.IsFalse(result.People.Any(person => person.ProviderIds != null
                && person.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var providerId)
                && int.TryParse(providerId, out var tmdbId)
                && tmdbId >= 2001
                && tmdbId <= 2999), "剧集人物结果不应再混入 crew。 ");
        }

        [TestMethod]
        public async Task GetMetadataByTMDB_PrefersAggregateCastAndExcludesCrewFromFinalPeople()
        {
            var info = new SeriesInfo
            {
                Name = "aggregate 剧集",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetaSharkPlugin.ProviderId, "Tmdb_263330" },
                    { MetadataProvider.Tmdb.ToString(), "263330" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbPosterConfig(tmdbApi);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var seededSeries = new TvShow
            {
                Id = 263330,
                Name = "aggregate 剧集",
                OriginalName = "Aggregate Series",
                Overview = "TMDb seeded series overview",
                FirstAirDate = new DateTime(2026, 1, 1),
                VoteAverage = 8.1,
                EpisodeRunTime = new List<int>(),
                ContentRatings = new ResultContainer<ContentRating>
                {
                    Results = new List<ContentRating>(),
                },
            };

            SetTmdbSeriesCredits(
                seededSeries,
                new[]
                {
                    CreateCastCredit(2394448, "三浦千幸", "Yuki (voice)", 0, "/actor-1.jpg"),
                },
                new[]
                {
                    CreateCrewCredit(2358885, "池田临太郎", "Writing", "Series Composition"),
                    CreateCrewCredit(5104896, "鵜飼有志", "Writing", "Original Story"),
                    CreateCrewCredit(3097346, "笠原周造", "Production", "Executive Producer"),
                });
            SetTmdbSeriesAggregateCredits(
                seededSeries,
                new[]
                {
                    CreateAggregateCastCredit(2394448, "三浦千幸", "Yuki (voice)", 0, 11, "/actor-1.jpg"),
                    CreateAggregateCastCredit(2883981, "土屋李央", "Mishiro (voice)", 1, 6, "/actor-2.jpg"),
                    CreateAggregateCastCredit(571993, "伊藤静", "Hakushi (voice)", 2, 5, "/actor-3.jpg"),
                    CreateAggregateCastCredit(1772522, "宫本侑芽", "Airi (voice)", 3, 4, "/actor-4.jpg"),
                    CreateAggregateCastCredit(2285491, "本泉莉奈", "Kyara (voice)", 4, 4, "/actor-5.jpg"),
                    CreateAggregateCastCredit(3114235, "丸冈和佳奈", "Keito (voice)", 5, 3, "/actor-6.jpg"),
                    CreateAggregateCastCredit(2363904, "若山诗音", "Kotoha (voice)", 6, 3, "/actor-7.jpg"),
                    CreateAggregateCastCredit(1325236, "田边留依", "Chie (voice)", 7, 3, "/actor-8.jpg"),
                    CreateAggregateCastCredit(1287794, "藤井幸代", "Yuki's Agent (voice)", 8, 3, "/actor-9.jpg"),
                    CreateAggregateCastCredit(3269285, "阿部菜摘子", "Moegi (voice)", 9, 3, "/actor-10.jpg"),
                    CreateAggregateCastCredit(1254135, "诸星堇", "Riko (voice)", 10, 2),
                    CreateAggregateCastCredit(1991799, "东内真理子", "Kaya (voice)", 11, 2),
                    CreateAggregateCastCredit(4077076, "瞳莎彩", "Mayumi (voice)", 12, 2),
                    CreateAggregateCastCredit(1072776, "竹达彩奈", "Sumiyaka (voice)", 13, 2),
                    CreateAggregateCastCredit(1325949, "水濑祈", "Kinko (voice)", 14, 1),
                    CreateAggregateCastCredit(3046161, "本村玲奈", "Aoi (voice)", 15, 1),
                },
                Array.Empty<Dictionary<string, object?>>());
            SeedTmdbSeries(tmdbApi, 263330, "zh-CN", seededSeries);

            foreach (var actor in new[]
                     {
                         (2394448, "三浦千幸"),
                         (2883981, "土屋李央"),
                         (571993, "伊藤静"),
                         (1772522, "宫本侑芽"),
                         (2285491, "本泉莉奈"),
                         (3114235, "丸冈和佳奈"),
                         (2363904, "若山诗音"),
                         (1325236, "田边留依"),
                         (1287794, "藤井幸代"),
                         (3269285, "阿部菜摘子"),
                         (1254135, "诸星堇"),
                         (1991799, "东内真理子"),
                         (4077076, "瞳莎彩"),
                         (1072776, "竹达彩奈"),
                         (1325949, "水濑祈"),
                         (3046161, "本村玲奈"),
                     })
            {
                SeedTmdbPerson(tmdbApi, actor.Item1, actor.Item2, language: "zh-CN");
            }

            var provider = new SeriesProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.People);
            Assert.AreEqual(Configuration.PluginConfiguration.MAXCASTMEMBERS, result.People.Count, "aggregate cast 应作为剧集人物主来源，并按演员上限截断。 ");
            CollectionAssert.AreEqual(
                new[] { 2394448, 2883981, 571993, 1772522, 2285491, 3114235, 2363904, 1325236, 1287794, 3269285, 1254135, 1991799, 4077076, 1072776, 1325949 },
                result.People.Select(GetTmdbId).ToArray(),
                "aggregate cast 应覆盖 credits cast，并保持顺序。 ");
            Assert.IsTrue(result.People.All(person => person.Type == PersonKind.Actor), "剧集人物结果应只保留演员。 ");
            Assert.IsFalse(result.People.Any(person => string.Equals(person.Name, "池田临太郎", StringComparison.Ordinal)));
            Assert.IsFalse(result.People.Any(person => string.Equals(person.Name, "鵜飼有志", StringComparison.Ordinal)));
            Assert.IsFalse(result.People.Any(person => string.Equals(person.Name, "笠原周造", StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task GetMetadataByTMDB_WithBlankRawAndBlankZhCnFallback_StillStampsCurrentPeopleRefreshState()
        {
            var info = new SeriesInfo
            {
                Name = "空人物剧集",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetaSharkPlugin.ProviderId, "Tmdb_9202" },
                    { MetadataProvider.Tmdb.ToString(), "9202" },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbPosterConfig(tmdbApi);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var seededSeries = new TvShow
            {
                Id = 9202,
                Name = "空人物剧集",
                OriginalName = "No Accepted People Series",
                Overview = "TMDb seeded series overview",
                FirstAirDate = new DateTime(2024, 2, 2),
                VoteAverage = 7.9,
                EpisodeRunTime = new List<int>(),
                ContentRatings = new ResultContainer<ContentRating>
                {
                    Results = new List<ContentRating>(),
                },
            };

            SetTmdbSeriesCredits(
                seededSeries,
                new[]
                {
                    CreateCastCredit(1101, string.Empty, "角色A", 0, "/actor-rejected.jpg"),
                },
                new[]
                {
                    CreateCrewCredit(2101, string.Empty, "Writing", "Screenplay", "/writer-rejected.jpg"),
                    CreateCrewCredit(2199, "Ignored Crew", "Art", "Art Direction"),
                });
            SeedTmdbSeries(tmdbApi, 9202, "zh-CN", seededSeries);

            SeedTmdbPerson(tmdbApi, 1101, string.Empty, language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 1101, "Actor Hans", language: "zh-Hans");
            SeedTmdbPerson(tmdbApi, 1101, "Actor Zh", language: "zh");
            SeedTmdbPersonTranslations(tmdbApi, 1101, CreatePersonTranslation("zh", "CN", string.Empty), CreatePersonTranslation("zh", "TW", "這個翻譯演員"));

            SeedTmdbPerson(tmdbApi, 2101, string.Empty, language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 2101, "Writer Hans", language: "zh-Hans");
            SeedTmdbPerson(tmdbApi, 2101, "Writer Zh", language: "zh");
            SeedTmdbPersonTranslations(tmdbApi, 2101, CreatePersonTranslation("zh", "CN", string.Empty), CreatePersonTranslation("zh", "TW", "這個翻譯編劇"));

            var provider = new SeriesProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsTrue(result.HasMetadata);
            Assert.IsTrue(result.People == null || result.People.Count == 0, "raw 为空且精确 zh-CN 详情/翻译也为空时应拒绝，即使其他变体有值。 ");
            Assert.IsFalse(result.Item.ProviderIds?.ContainsKey("MetaSharkPeopleRefreshState") ?? false, "即使没有任何 accepted people，provider 也不应把内部 state 写回 provider ids。 ");
        }

        [TestMethod]
        public async Task GetMetadataByTMDB_WithExistingPersonEntityNameMismatch_OverwritesLocalPersonNameToResolvedTmdbName()
        {
            var info = new SeriesInfo
            {
                Name = "人物对齐剧集",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetaSharkPlugin.ProviderId, "Tmdb_9301" },
                    { MetadataProvider.Tmdb.ToString(), "9301" },
                },
            };

            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbPosterConfig(tmdbApi);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            var seededSeries = new TvShow
            {
                Id = 9301,
                Name = "人物对齐剧集",
                OriginalName = "Series Person Alignment",
                Overview = "TMDb seeded series overview",
                FirstAirDate = new DateTime(2024, 3, 1),
                VoteAverage = 8.2,
                EpisodeRunTime = new List<int>(),
                ContentRatings = new ResultContainer<ContentRating>
                {
                    Results = new List<ContentRating>(),
                },
            };

            SetTmdbSeriesCredits(
                seededSeries,
                new[]
                {
                    CreateCastCredit(1201637, "三瓶 由布子", "角色A", 0, "/actor.jpg"),
                },
                Array.Empty<Dictionary<string, object?>>());
            SeedTmdbSeries(tmdbApi, 9301, "zh-CN", seededSeries);
            SeedTmdbPerson(tmdbApi, 1201637, "三瓶由布子", language: "zh-CN");

            var existingPerson = new TrackingPerson
            {
                Name = "三瓶 由布子",
            };
            existingPerson.SetProviderId(MetadataProvider.Tmdb, "1201637");

            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { existingPerson });

            var provider = new SeriesProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            var actor = GetPersonByTmdbId(result.People, 1201637);
            Assert.AreEqual("三瓶由布子", actor.Name, "剧集演员名应对齐 TMDb 解析结果。 ");
            Assert.AreEqual("三瓶由布子", existingPerson.Name, "已有 Person 实体名与 TMDb 不一致时，应被覆盖为 TMDb 名称。 ");
            Assert.AreEqual(1, existingPerson.MetadataChangedCallCount, "对齐已有 Person 实体名时应触发 metadata changed。 ");
            Assert.AreEqual(1, existingPerson.UpdateToRepositoryCallCount, "对齐已有 Person 实体名时应写回仓库。 ");
            Assert.AreEqual(ItemUpdateType.MetadataEdit, existingPerson.LastUpdateReason);
        }

        [TestMethod]
        public async Task SeriesProviderLog_GetSearchResults_WithInvalidTmdbId_UsesChineseDecisionMessage()
        {
            var providerLogger = new Mock<ILogger>();
            providerLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var providerLoggerFactory = new Mock<ILoggerFactory>();
            providerLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(providerLogger.Object);

            using var apiLoggerFactory = LoggerFactory.Create(builder => { });
            var provider = new SeriesProvider(
                new DefaultHttpClientFactory(),
                providerLoggerFactory.Object,
                new Mock<ILibraryManager>().Object,
                new Mock<IHttpContextAccessor>().Object,
                new DoubanApi(apiLoggerFactory),
                new TmdbApi(apiLoggerFactory),
                new OmdbApi(apiLoggerFactory),
                new ImdbApi(apiLoggerFactory));

            var results = (await provider.GetSearchResults(
                new SeriesInfo
                {
                    Name = string.Empty,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), "abc" },
                    },
                },
                CancellationToken.None).ConfigureAwait(false)).ToList();

            Assert.AreEqual(0, results.Count);
            LogAssert.AssertLoggedOnce(
                providerLogger,
                LogLevel.Information,
                expectException: false,
                originalFormatContains: "[MetaShark] {Message}",
                messageContains: new[] { "开始搜索剧集候选. name: " });
            LogAssert.AssertLoggedOnce(
                providerLogger,
                LogLevel.Information,
                expectException: false,
                originalFormatContains: "[MetaShark] {Message}",
                messageContains: new[] { "跳过显式 TMDb ID 匹配，provider id 无效. rawProviderId: 'abc' fallbackToTitleSearch: False" });
        }

        [TestMethod]
        public async Task SeriesProviderLog_GetMetadataByTmdb_UsesChineseSummary()
        {
            var providerLogger = new Mock<ILogger>();
            providerLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var providerLoggerFactory = new Mock<ILoggerFactory>();
            providerLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(providerLogger.Object);

            using var apiLoggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(apiLoggerFactory);
            SeedTmdbSeries(tmdbApi, 45247, "zh-CN", "花牌情缘");

            var provider = new SeriesProvider(
                new DefaultHttpClientFactory(),
                providerLoggerFactory.Object,
                new Mock<ILibraryManager>().Object,
                new Mock<IHttpContextAccessor>().Object,
                new DoubanApi(apiLoggerFactory),
                tmdbApi,
                new OmdbApi(apiLoggerFactory),
                new ImdbApi(apiLoggerFactory));

            var result = await provider.GetMetadata(
                new SeriesInfo
                {
                    Name = "花牌情缘",
                    MetadataLanguage = "zh-CN",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetaSharkPlugin.ProviderId, "Tmdb_45247" },
                        { MetadataProvider.Tmdb.ToString(), "45247" },
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
                    "开始获取剧集元数据. name: 花牌情缘",
                    "metaSource: Tmdb",
                    "enableTmdb: True",
                });
            LogAssert.AssertLoggedOnce(
                providerLogger,
                LogLevel.Information,
                expectException: false,
                originalFormatContains: "[MetaShark] {Message}",
                messageContains: new[] { "通过 TMDb 获取剧集元数据. tmdbId: \"45247\"" });
        }

    }

    internal sealed class TrackingPerson : Person
    {
        public int MetadataChangedCallCount { get; private set; }

        public int UpdateToRepositoryCallCount { get; private set; }

        public ItemUpdateType? LastUpdateReason { get; private set; }

        public override ItemUpdateType OnMetadataChanged()
        {
            this.MetadataChangedCallCount++;
            return ItemUpdateType.MetadataEdit;
        }

        public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
        {
            this.UpdateToRepositoryCallCount++;
            this.LastUpdateReason = updateReason;
            return Task.CompletedTask;
        }
    }
}
