using System.Collections;
using System.Reflection;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
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
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;
using ContentRating = TMDbLib.Objects.TvShows.ContentRating;
using ResultContainer = TMDbLib.Objects.General.ResultContainer<TMDbLib.Objects.TvShows.ContentRating>;
using TmdbPerson = TMDbLib.Objects.People.Person;
using SearchTv = TMDbLib.Objects.Search.SearchTv;
using TvShow = TMDbLib.Objects.TvShows.TvShow;
using Translation = TMDbLib.Objects.General.Translation;
using TranslationsContainer = TMDbLib.Objects.General.TranslationsContainer;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class SeriesProviderSearchMissingMetadataRequestTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-series-provider-request-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        [TestMethod]
        public async Task GetMetadata_ShouldSaveCandidate_WhenInfoRepresentsUserRefresh()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 456, "zh-CN", "示例剧集");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateSeriesInfo();
            info.IsAutomated = false;
            var currentSeries = new Series
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, true))
                .Returns(currentSeries);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = null },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            var candidate = store.Peek(currentSeries.Id);
            Assert.IsNotNull(candidate, "手动单项剧集 refresh 命中 TMDb provider 时，应保存一次性 overwrite candidate。 ");
            Assert.AreEqual(currentSeries.Id, candidate!.ItemId);
            Assert.AreEqual(info.Path, candidate.ItemPath);
            Assert.AreEqual(result.People?.Count ?? 0, candidate.ExpectedPeopleCount, "candidate 应记录 series provider 实际产出的期望 people 数。 ");
        }


        [TestMethod]
        public async Task GetMetadata_ShouldSaveAcceptedPeopleCount_WhenDoubanMetadataResolvesTmdbPeopleDuringUserRefresh()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            SeedDoubanSubject(
                doubanApi,
                new DoubanSubject
                {
                    Sid = "series-douban-refresh-456",
                    Name = "示例剧集",
                    OriginalName = "Sample Series",
                    Year = 2024,
                    Rating = 8.6f,
                    Genre = "剧情",
                    Intro = "Douban seeded series overview",
                    Screen = "2024-02-03",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000007.webp",
                });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeriesSearchResults(
                tmdbApi,
                "示例剧集",
                "zh-CN",
                new SearchTv
                {
                    Id = 456,
                    Name = "示例剧集",
                    OriginalName = "Sample Series",
                    FirstAirDate = new DateTime(2024, 1, 1),
                });
            var seededSeries = new TvShow
            {
                Id = 456,
                Name = "示例剧集",
                OriginalName = "Sample Series",
                Overview = "TMDb seeded series overview",
                FirstAirDate = new DateTime(2024, 1, 1),
                VoteAverage = 8.8,
                EpisodeRunTime = new List<int>(),
                ContentRatings = new ResultContainer
                {
                    Results = new List<ContentRating>(),
                },
            };
            SetTmdbSeriesCredits(
                seededSeries,
                new[]
                {
                    CreateCastCredit(5101, "Raw Actor Accepted", "角色A", 0),
                    CreateCastCredit(5102, "Raw Actor Rejected", "角色B", 1),
                },
                new[]
                {
                    CreateCrewCredit(5201, "Raw Director Accepted", "Production", "Director"),
                    CreateCrewCredit(5299, "Ignored Crew", "Art", "Art Direction"),
                });
            SeedTmdbSeries(tmdbApi, 456, "zh-CN", seededSeries);
            SeedTmdbPerson(tmdbApi, 5101, "剧集演员大陆名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 5102, string.Empty, language: "zh-CN");
            SeedTmdbPersonTranslations(tmdbApi, 5102);
            SeedTmdbPerson(tmdbApi, 5201, "剧集导演大陆名", language: "zh-CN");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = new SeriesInfo
            {
                Name = "示例剧集",
                Path = "/library/tv/sample-series",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "series-douban-refresh-456",
                    [MetaSharkPlugin.ProviderId] = $"{MetaSource.Douban}_series-douban-refresh-456",
                },
                IsAutomated = false,
            };
            var currentSeries = new Series
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, true))
                .Returns(currentSeries);

            var provider = new SeriesProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = null },
                doubanApi,
                tmdbApi,
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                store);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual(2, result.People?.Count ?? 0, "candidate 计数应基于实际接受的 TMDb people，而不是 Douban celebrities 或未通过 strict helper 的原始 credits。 ");
            var candidate = store.Peek(currentSeries.Id);
            Assert.IsNotNull(candidate, "Douban 元数据分支在 user refresh 下补回 TMDb people 时，也应保存一次性 overwrite candidate。 ");
            Assert.AreEqual(2, candidate!.ExpectedPeopleCount);
            Assert.AreEqual(result.People?.Count ?? 0, candidate.ExpectedPeopleCount, "candidate 应记录 Douban 分支最终实际接受的 TMDb people 数。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenInfoRepresentsAutomatedRefresh()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 456, "zh-CN", "示例剧集");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateSeriesInfo();
            info.IsAutomated = true;
            var currentSeries = new Series
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, true))
                .Returns(currentSeries);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = null },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsNull(store.Peek(currentSeries.Id), "自动刷新不应为单项 one-shot overwrite 保存 candidate。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_ForManualMatchRequest()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 456, "zh-CN", "示例剧集");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateSeriesInfo();
            info.IsAutomated = false;
            var currentSeries = new Series
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, true))
                .Returns(currentSeries);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                CreateManualMatchContextAccessor(),
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsNull(store.Peek(currentSeries.Id), "手动匹配 /Items/RemoteSearch/Apply 不应创建单项 overwrite candidate。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotResetQueuedCandidate_WhenExistingCandidateAlreadyPending()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 456, "zh-CN", "示例剧集");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateSeriesInfo();
            info.IsAutomated = false;
            var currentSeries = new Series
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            store.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = currentSeries.Id,
                ItemPath = currentSeries.Path,
                ExpectedPeopleCount = 17,
                OverwriteQueued = true,
            });

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, true))
                .Returns(currentSeries);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = null },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            var candidate = store.Peek(currentSeries.Id);
            Assert.IsNotNull(candidate, "follow-up overwrite refresh 期间不应把已排队 series candidate 清掉或重置。 ");
            Assert.AreEqual(17, candidate!.ExpectedPeopleCount, "已排队 series candidate 不应被 provider 新写入的未排队 candidate 覆盖。 ");
            Assert.IsTrue(candidate.OverwriteQueued, "已排队 series candidate 不应被重置为未排队。 ");
            Assert.AreEqual(currentSeries.Path, candidate.ItemPath);
        }

        private static SeriesProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IMovieSeriesPeopleOverwriteRefreshCandidateStore store,
            ILoggerFactory loggerFactory)
        {
            return new SeriesProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager,
                httpContextAccessor,
                new DoubanApi(loggerFactory),
                tmdbApi,
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                store);
        }

        private static SeriesInfo CreateSeriesInfo()
        {
            return new SeriesInfo
            {
                Name = "示例剧集",
                Path = "/library/tv/sample-series",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    [MetaSharkPlugin.ProviderId] = "Tmdb_456",
                    [MetadataProvider.Tmdb.ToString()] = "456",
                },
            };
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

        private static void SeedTmdbSeries(TmdbApi tmdbApi, int tmdbId, string language, string name)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"series-{tmdbId}-{language}-{language}",
                new TvShow
                {
                    Id = tmdbId,
                    Name = name,
                    OriginalName = name,
                    Overview = "TMDb seeded series overview",
                    FirstAirDate = new DateTime(2011, 10, 4),
                    VoteAverage = 8.8,
                    EpisodeRunTime = new List<int>(),
                    ContentRatings = new ResultContainer
                    {
                        Results = new List<ContentRating>(),
                    },
                },
                TimeSpan.FromMinutes(5));
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

        private static Dictionary<string, object?> CreateCastCredit(int id, string name, string character, int order)
        {
            return new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["Name"] = name,
                ["Character"] = character,
                ["Order"] = order,
            };
        }

        private static Dictionary<string, object?> CreateCrewCredit(int id, string name, string department, string job)
        {
            return new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["Name"] = name,
                ["Department"] = department,
                ["Job"] = job,
            };
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

        private static MemoryCache GetDoubanMemoryCache(DoubanApi doubanApi)
        {
            var memoryCacheField = typeof(DoubanApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "DoubanApi.memoryCache 未找到");

            var memoryCache = memoryCacheField!.GetValue(doubanApi) as MemoryCache;
            Assert.IsNotNull(memoryCache, "DoubanApi.memoryCache 不是有效的 MemoryCache");
            return memoryCache!;
        }

        private static void SeedDoubanSubject(DoubanApi doubanApi, DoubanSubject subject)
        {
            var cache = GetDoubanMemoryCache(doubanApi);
            cache.Set($"movie_{subject.Sid}", subject, TimeSpan.FromMinutes(5));
            cache.Set($"celebrities_{subject.Sid}", new List<DoubanCelebrity>(), TimeSpan.FromMinutes(5));
        }

        private static MemoryCache GetTmdbMemoryCache(TmdbApi tmdbApi)
        {
            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "TmdbApi.memoryCache 未找到");

            var memoryCache = memoryCacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(memoryCache, "TmdbApi.memoryCache 不是有效的 MemoryCache");
            return memoryCache!;
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
    }
}
