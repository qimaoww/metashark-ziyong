using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class SeasonImageProviderTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-season-image-provider-tests");
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
        public void GetImagesFallsBackToTmdbWhenDoubanBlocked()
        {
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var info = CreateSeason(libraryManagerStub, "第1季", 1, "season-douban", "series-douban", "34860");
            var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
            var doubanApi = DoubanApiTestHelper.CreateBlockedDoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            SeedTmdbSeasonImages(tmdbApi, 34860, 1, string.Empty, "第1季");
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                Task.Run(async () =>
                {
                    var provider = new SeasonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.IsTrue(images.Any(), "豆瓣季图拿不到时，只要存在 series TMDb id，就应回退到 TMDb 季图。");
                    Assert.AreEqual(tmdbApi.GetPosterUrl("/season-poster.jpg")?.ToString(), images[0].Url);
                }).GetAwaiter().GetResult();
            });
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

                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var info = CreateSeason(libraryManagerStub, "第1季", 1, "season-douban", "series-douban", "34860");
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动季图片链路不应再访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeasonImages(tmdbApi, 34860, 1, string.Empty, "第1季");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                WithLibraryManager(libraryManagerStub.Object, () =>
                {
                    Task.Run(async () =>
                    {
                        var provider = new SeasonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                        Assert.AreEqual(1, images.Count, "tmdb-only 自动季图片链路在存在 TMDb fallback 时应直接走 TMDb。");
                        Assert.AreEqual(tmdbApi.GetPosterUrl("/season-poster.jpg")?.ToString(), images[0].Url);
                    }).GetAwaiter().GetResult();
                });
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

                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var info = CreateSeason(libraryManagerStub, "第1季", 1, "season-douban", "series-douban", null);
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动季图片链路在没有 TMDb fallback 时也不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                WithLibraryManager(libraryManagerStub.Object, () =>
                {
                    Task.Run(async () =>
                    {
                        var provider = new SeasonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                        Assert.AreEqual(0, images.Count, "tmdb-only 自动季图片链路在没有插件内 TMDb fallback 时应直接返回空集合。");
                    }).GetAwaiter().GetResult();
                });
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
            }
        }

        [TestMethod]
        public void DefaultScraperPolicy_TmdbOnlyAutomaticImagesWithParentTmdbMetaSourceStillSkipDouban()
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

                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var info = CreateSeason(libraryManagerStub, "第1季", 1, "season-douban", "series-douban", "34860", MetaSource.Tmdb);
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 非手动季图片链路即使 parent series meta-source=TMDb 也不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeasonImages(tmdbApi, 34860, 1, string.Empty, "第1季");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                WithLibraryManager(libraryManagerStub.Object, () =>
                {
                    Task.Run(async () =>
                    {
                        var provider = new SeasonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                        Assert.AreEqual(1, images.Count, "tmdb-only 非手动季图片链路即使 parent series meta-source=TMDb 也应保持 TMDb fallback。 ");
                        Assert.AreEqual(tmdbApi.GetPosterUrl("/season-poster.jpg")?.ToString(), images[0].Url);
                    }).GetAwaiter().GetResult();
                });
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
        }

        [TestMethod]
        public void ManualRemoteImageSearch_WithoutSeasonDoubanId_UsesTmdbFallbackWithoutException()
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

                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var info = CreateSeason(libraryManagerStub, "第1季", 1, "season-douban", "series-douban", "34860", MetaSource.Tmdb);
                info.ProviderIds.Remove(BaseProvider.DoubanProviderId);
                Assert.IsFalse(info.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId), "测试前提失败：season 不应保留 DoubanId。");

                var httpContextAccessor = CreateManualRemoteImageContextAccessor();
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "手动 RemoteImages 在缺少 season DoubanId 时不应意外访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeasonImages(tmdbApi, 34860, 1, string.Empty, "第1季");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                WithLibraryManager(libraryManagerStub.Object, () =>
                {
                    Task.Run(async () =>
                    {
                        var provider = new SeasonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                        Assert.AreEqual(1, images.Count, "手动 RemoteImages 在缺少 season DoubanId 时应保持稳定并回退到 TMDb 季图。 ");
                        Assert.AreEqual(tmdbApi.GetPosterUrl("/season-poster.jpg")?.ToString(), images[0].Url);
                    }).GetAwaiter().GetResult();
                });
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
        }

        [TestMethod]
        public void ManualRemoteImageSearch_TmdbOnly_ReturnsDoubanSeasonImage()
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

                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var info = CreateSeason(libraryManagerStub, "第1季", 1, "season-douban", "series-douban", "34860", MetaSource.Tmdb);
                var httpContextAccessor = CreateManualRemoteImageContextAccessor();
                var doubanApi = new DoubanApi(this.loggerFactory);
                SeedDoubanSubject(doubanApi, new DoubanSubject
                {
                    Sid = "season-douban",
                    Name = "辛普森一家 第1季",
                    Category = "电视剧",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p1234567890.webp",
                });
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeasonImages(tmdbApi, 34860, 1, string.Empty, "第1季");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                WithLibraryManager(libraryManagerStub.Object, () =>
                {
                    Task.Run(async () =>
                    {
                        var provider = new SeasonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                        Assert.AreEqual(1, images.Count, "手动 RemoteImages 搜索路径应返回唯一的季主图。");
                        Assert.IsFalse(images[0].Url?.Contains("image.tmdb.org", StringComparison.OrdinalIgnoreCase) == true, "tmdb-only 下的手动季图搜索不应因为父级 series meta-source=TMDb 而错误回退到 TMDb URL。");
                        Assert.IsTrue(images[0].Url?.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase) == true, "手动 RemoteImages 搜索路径在 tmdb-only 下仍应返回 Douban 季图。当前失败说明 Douban 手动链路被误伤。\n实际 URL: " + images[0].Url);
                    }).GetAwaiter().GetResult();
                });
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
        }

        private static Season CreateSeason(Mock<ILibraryManager> libraryManagerStub, string name, int indexNumber, string seasonDoubanId, string seriesDoubanId, string? seriesTmdbId, MetaSource? seriesMetaSource = null)
        {
            var seriesProviderIds = new Dictionary<string, string>
            {
                { BaseProvider.DoubanProviderId, seriesDoubanId },
            };
            if (seriesMetaSource.HasValue)
            {
                seriesProviderIds[MetaSharkPlugin.ProviderId] = seriesMetaSource.Value.ToString();
            }

            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = "辛普森一家",
                PreferredMetadataLanguage = "zh",
                ProviderIds = seriesProviderIds,
            };
            if (!string.IsNullOrEmpty(seriesTmdbId))
            {
                series.SetProviderId(MetadataProvider.Tmdb, seriesTmdbId);
            }

            libraryManagerStub.Setup(x => x.GetItemById(series.Id)).Returns((BaseItem)series);

            return new Season
            {
                Name = name,
                IndexNumber = indexNumber,
                PreferredMetadataLanguage = "zh",
                SeriesId = series.Id,
                SeriesName = series.Name,
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, seasonDoubanId },
                },
            };
        }

        private static void WithLibraryManager(ILibraryManager libraryManager, Action action)
        {
            var originalLibraryManager = BaseItem.LibraryManager;
            BaseItem.LibraryManager = libraryManager;

            try
            {
                action();
            }
            finally
            {
                BaseItem.LibraryManager = originalLibraryManager;
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

        private static void SeedTmdbSeasonImages(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, string language, string seasonName)
        {
            var season = new TvSeason
            {
                Name = seasonName,
                Overview = "TMDb seeded season overview",
                AirDate = new DateTime(2015, 2, 1),
            };
            SetTmdbImages(season, ("Posters", new List<ImageData>
            {
                CreateImageData("/season-poster.jpg", "zh", 1000, 1500),
            }));

            GetTmdbMemoryCache(tmdbApi).Set(
                $"season-{seriesTmdbId}-s{seasonNumber}-{language}-{language}",
                season,
                TimeSpan.FromMinutes(5));
        }

        private static void SetTmdbImages(object target, params (string PropertyName, IList<ImageData> Images)[] imageSets)
        {
            var imagesProperty = target.GetType().GetProperty("Images", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(imagesProperty, $"{target.GetType().Name}.Images 未定义");

            var images = Activator.CreateInstance(imagesProperty!.PropertyType);
            Assert.IsNotNull(images, $"无法创建 {imagesProperty.PropertyType.Name} 实例");

            foreach (var imageSet in imageSets)
            {
                var property = imagesProperty.PropertyType.GetProperty(imageSet.PropertyName, BindingFlags.Instance | BindingFlags.Public);
                Assert.IsNotNull(property, $"{imagesProperty.PropertyType.Name}.{imageSet.PropertyName} 未定义");
                property!.SetValue(images, imageSet.Images.ToList());
            }

            imagesProperty.SetValue(target, images);
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

        private static IHttpContextAccessor CreateManualRemoteImageContextAccessor(string itemId = "1")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = $"/Items/{itemId}/RemoteImages";
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
    }
}
