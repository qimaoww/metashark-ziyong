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
using TmdbMovie = TMDbLib.Objects.Movies.Movie;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class MovieImageProviderTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-movie-image-provider-tests");
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
            var info = new MediaBrowser.Controller.Entities.Movies.Movie()
            {
                Name = "秒速5厘米",
                PreferredMetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string> { { BaseProvider.DoubanProviderId, "2043546" }, { MetadataProvider.Tmdb.ToString(), "38142" } }
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
                var provider = new MovieImageProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetImages(info, CancellationToken.None);
                Assert.IsNotNull(result);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetImagesFromTMDB()
        {
            var info = new MediaBrowser.Controller.Entities.Movies.Movie()
            {
                PreferredMetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), "752" }, { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() } }
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
                var provider = new MovieImageProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetImages(info, CancellationToken.None);
                Assert.IsNotNull(result);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetImageResponse()
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
                var provider = new MovieImageProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetImageResponse(new Uri("https://img1.doubanio.com/view/photo/m/public/p2893270877.jpg", UriKind.Absolute), CancellationToken.None);
                Assert.IsNotNull(result);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetImagesFallsBackToTmdbWhenDoubanBlocked()
        {
            var info = new MediaBrowser.Controller.Entities.Movies.Movie()
            {
                Name = "秒速5厘米",
                PreferredMetadataLanguage = "zh",
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
                    var provider = new MovieImageProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
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

                var info = new MediaBrowser.Controller.Entities.Movies.Movie()
                {
                    Name = "秒速5厘米",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "2043546" },
                        { MetadataProvider.Tmdb.ToString(), "38142" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动电影图片链路不应再访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbMovieDetails(tmdbApi, 38142, "zh");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new MovieImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.IsTrue(images.Any(), "tmdb-only 自动图片链路在存在有效 TMDb id 时应改道到 TMDb。");
                    Assert.IsTrue(images.Any(image => image.Url == tmdbApi.GetPosterUrl("/movie-poster.jpg")?.ToString()), "应返回 TMDb 主海报。");
                    Assert.IsFalse(images.Any(image => image.Url?.Contains("douban", StringComparison.OrdinalIgnoreCase) == true), "tmdb-only 自动图片链路不应再泄漏 Douban 图片 URL。");
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

                var info = new MediaBrowser.Controller.Entities.Movies.Movie()
                {
                    Name = "只有豆瓣海报",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "only-douban" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动电影图片链路在没有 TMDb fallback 时也不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new MovieImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.AreEqual(0, images.Count, "tmdb-only 自动图片链路在没有插件内 TMDb fallback 时应直接返回空集合。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
            }
        }

        [TestMethod]
        public void ManualRemoteImages_TmdbMovieReturnsAllImageLanguages()
        {
            var tmdbId = 990001;
            var language = "zh";
            var images = CreateMultilingualMovieImages();
            var info = new MediaBrowser.Controller.Entities.Movies.Movie()
            {
                PreferredMetadataLanguage = language,
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                    { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = HttpMethods.Get;
            httpContext.Request.Path = "/Items/movie-id/RemoteImages";
            httpContext.Request.QueryString = new QueryString("?includeAllLanguages=true");
            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
            var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "TMDb 手动电影图片链路不应访问 Douban。");
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            SeedTmdbMovieDetails(
                tmdbApi,
                tmdbId,
                language,
                "/movie-poster-zh.jpg",
                "/movie-backdrop-zh.jpg",
                images.Posters,
                images.Backdrops,
                images.Logos,
                new List<ImageData>());
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            Task.Run(async () =>
            {
                var provider = new MovieImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                var remoteImages = (await provider.GetImages(info, CancellationToken.None)).ToList();

                Assert.AreEqual(12, remoteImages.Count, "手动 RemoteImages 应返回 TMDb 的全部 poster/backdrop/logo 候选。");
                AssertImageLanguages(remoteImages.Where(image => image.Type == ImageType.Primary), "zh", "en", "ja", null);
                AssertImageLanguages(remoteImages.Where(image => image.Type == ImageType.Backdrop), "zh", "en", "ja", null);
                AssertImageLanguages(remoteImages.Where(image => image.Type == ImageType.Logo), "zh", "en", "ja", null);

                var englishPoster = remoteImages.Single(image => image.Url == tmdbApi.GetPosterUrl("/movie-poster-en.jpg")?.ToString());
                Assert.AreEqual(MetaSharkPlugin.PluginName, englishPoster.ProviderName);
                Assert.AreEqual(ImageType.Primary, englishPoster.Type);
                Assert.AreEqual("en", englishPoster.Language);
                Assert.AreEqual(8.5, englishPoster.CommunityRating);
                Assert.AreEqual(10, englishPoster.VoteCount);
                Assert.AreEqual(1000, englishPoster.Width);
                Assert.AreEqual(1500, englishPoster.Height);
                Assert.AreEqual(RatingType.Score, englishPoster.RatingType);

                Assert.IsTrue(remoteImages.Any(image => image.Url == tmdbApi.GetBackdropUrl("/movie-backdrop-ja.jpg")?.ToString() && image.Language == "ja"), "应使用 backdrop URL helper 并保留日语语言。");
                Assert.IsTrue(remoteImages.Any(image => image.Url == tmdbApi.GetLogoUrl("/movie-logo-no-language.png")?.ToString() && image.Language == null), "应使用 logo URL helper 并保留无语言图片。");
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void ManualRemoteImages_DoubanMovieAlsoReturnsAllTmdbImageLanguages()
        {
            var tmdbId = 990003;
            var sid = "douban-manual-movie";
            var language = "zh";
            var images = CreateMultilingualMovieImages();
            var info = new MediaBrowser.Controller.Entities.Movies.Movie()
            {
                Name = "豆瓣来源多语言电影",
                PreferredMetadataLanguage = language,
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, sid },
                    { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = HttpMethods.Get;
            httpContext.Request.Path = "/Items/movie-id/RemoteImages";
            httpContext.Request.QueryString = new QueryString("?includeAllLanguages=true");
            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
            var doubanApi = new DoubanApi(this.loggerFactory);
            SeedDoubanSubject(doubanApi, new DoubanSubject
            {
                Sid = sid,
                Name = "豆瓣来源多语言电影",
                Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p990003.jpg",
                Language = "日语",
            });
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            SeedTmdbMovieDetails(
                tmdbApi,
                tmdbId,
                language,
                "/movie-poster-zh.jpg",
                "/movie-backdrop-zh.jpg",
                images.Posters,
                images.Backdrops,
                images.Logos,
                new List<ImageData>());
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            Task.Run(async () =>
            {
                var provider = new MovieImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                var remoteImages = (await provider.GetImages(info, CancellationToken.None)).ToList();

                Assert.IsTrue(remoteImages.Any(image => image.Type == ImageType.Primary && image.Url?.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase) == true), "手动 Douban 来源应保留豆瓣中文海报。");
                Assert.AreEqual(5, remoteImages.Count(image => image.Type == ImageType.Primary), "手动 Douban 来源应同时追加 TMDb 全语言海报候选。");
                AssertImageLanguages(remoteImages.Where(image => image.Type == ImageType.Primary && image.Url?.Contains("tmdb", StringComparison.OrdinalIgnoreCase) == true), "zh", "en", "ja", null);
                AssertImageLanguages(remoteImages.Where(image => image.Type == ImageType.Backdrop), "zh", "en", "ja", null);
                AssertImageLanguages(remoteImages.Where(image => image.Type == ImageType.Logo), "zh", "en", "ja", null);
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void AutomaticImages_TmdbMovieKeepsSelectedPosterAndBackdropOnly()
        {
            var tmdbId = 990002;
            var language = "zh";
            var images = CreateMultilingualMovieImages();
            var info = new MediaBrowser.Controller.Entities.Movies.Movie()
            {
                PreferredMetadataLanguage = language,
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                    { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
            var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "TMDb 自动电影图片链路不应访问 Douban。");
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            SeedTmdbMovieDetails(
                tmdbApi,
                tmdbId,
                language,
                "/movie-poster-zh.jpg",
                "/movie-backdrop-zh.jpg",
                images.Posters,
                images.Backdrops,
                images.Logos,
                new List<ImageData>());
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            Task.Run(async () =>
            {
                var provider = new MovieImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                var remoteImages = (await provider.GetImages(info, CancellationToken.None)).ToList();

                Assert.AreEqual(2, remoteImages.Count, "非手动刷新应继续只返回详情中选中的 poster/backdrop。");
                Assert.IsTrue(remoteImages.Any(image => image.Type == ImageType.Primary && image.Url == tmdbApi.GetPosterUrl("/movie-poster-zh.jpg")?.ToString() && image.Language == language), "应保留选中的本地化 poster。");
                Assert.IsTrue(remoteImages.Any(image => image.Type == ImageType.Backdrop && image.Url == tmdbApi.GetBackdropUrl("/movie-backdrop-zh.jpg")?.ToString() && image.Language == language), "应保留选中的本地化 backdrop。");
                Assert.IsFalse(remoteImages.Any(image => image.Url == tmdbApi.GetPosterUrl("/movie-poster-en.jpg")?.ToString()), "非手动刷新不应返回未选中的英文 poster。");
                Assert.IsFalse(remoteImages.Any(image => image.Url == tmdbApi.GetBackdropUrl("/movie-backdrop-ja.jpg")?.ToString()), "非手动刷新不应返回未选中的日语 backdrop。");
            }).GetAwaiter().GetResult();
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

        private static void SeedTmdbMovieDetails(TmdbApi tmdbApi, int tmdbId, string language)
        {
            SeedTmdbMovieDetails(
                tmdbApi,
                tmdbId,
                language,
                "/movie-poster.jpg",
                "/movie-backdrop.jpg",
                new List<ImageData> { CreateImageData("/movie-poster.jpg", "zh", 1000, 1500) },
                new List<ImageData> { CreateImageData("/movie-backdrop.jpg", "zh", 1920, 1080) },
                new List<ImageData>(),
                new List<ImageData> { CreateImageData("/movie-logo.png", "zh", 500, 250) });
        }

        private static void SeedTmdbMovieDetails(
            TmdbApi tmdbApi,
            int tmdbId,
            string language,
            string posterPath,
            string backdropPath,
            IList<ImageData> posters,
            IList<ImageData> backdrops,
            IList<ImageData> logos,
            IList<ImageData> movieLogos)
        {
            var cache = GetTmdbMemoryCache(tmdbApi);
            var movie = new TmdbMovie
            {
                Id = tmdbId,
                Title = "秒速5厘米",
                OriginalTitle = "秒速5センチメートル",
                PosterPath = posterPath,
                BackdropPath = backdropPath,
            };
            SetTmdbImages(movie, ("Logos", movieLogos));

            cache.Set($"movie-{tmdbId}-{language}-{language}", movie, TimeSpan.FromMinutes(5));
            cache.Set(
                $"movie-images-{tmdbId}--",
                new ImagesWithId
                {
                    Posters = posters.ToList(),
                    Backdrops = backdrops.ToList(),
                    Logos = logos.ToList(),
                },
                TimeSpan.FromMinutes(5));
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

        private static (List<ImageData> Posters, List<ImageData> Backdrops, List<ImageData> Logos) CreateMultilingualMovieImages()
        {
            return (
                new List<ImageData>
                {
                    CreateImageData("/movie-poster-zh.jpg", "zh", 1000, 1500),
                    CreateImageData("/movie-poster-en.jpg", "en", 1000, 1500),
                    CreateImageData("/movie-poster-ja.jpg", "ja", 1000, 1500),
                    CreateImageData("/movie-poster-no-language.jpg", null, 1000, 1500),
                },
                new List<ImageData>
                {
                    CreateImageData("/movie-backdrop-zh.jpg", "zh", 1920, 1080),
                    CreateImageData("/movie-backdrop-en.jpg", "en", 1920, 1080),
                    CreateImageData("/movie-backdrop-ja.jpg", "ja", 1920, 1080),
                    CreateImageData("/movie-backdrop-no-language.jpg", null, 1920, 1080),
                },
                new List<ImageData>
                {
                    CreateImageData("/movie-logo-zh.png", "zh", 500, 250),
                    CreateImageData("/movie-logo-en.png", "en", 500, 250),
                    CreateImageData("/movie-logo-ja.png", "ja", 500, 250),
                    CreateImageData("/movie-logo-no-language.png", null, 500, 250),
                });
        }

        private static void AssertImageLanguages(IEnumerable<RemoteImageInfo> images, params string?[] expectedLanguages)
        {
            var actualLanguages = images.Select(image => image.Language).ToList();
            Assert.AreEqual(expectedLanguages.Length, actualLanguages.Count, "图片数量应与期望语言数量一致。");
            foreach (var expectedLanguage in expectedLanguages)
            {
                Assert.IsTrue(actualLanguages.Any(language => language == expectedLanguage), $"缺少语言 {expectedLanguage ?? "<null>"} 的图片。");
            }
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
