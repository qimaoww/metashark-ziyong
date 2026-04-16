using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
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
                    },
                },
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

    }
}
