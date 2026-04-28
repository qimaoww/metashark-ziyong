using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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
    public class PersonImageProviderTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-person-image-provider-tests");
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
        public void DefaultScraperPolicy_TmdbOnlyAutomaticImagesSkipDoubanAndUseTmdbFallback()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;

                var info = new Person
                {
                    Name = "周迅",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "27257290" },
                        { MetadataProvider.Tmdb.ToString(), "287" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动人物图片链路不应再访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbPersonProfiles(tmdbApi, 287);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new PersonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.IsTrue(images.Any(), "tmdb-only 自动人物图片链路在存在有效 TMDb id 时应改道到 TMDb。");
                    Assert.IsTrue(images.Any(image => image.Url == tmdbApi.GetProfileUrl("/person-profile.jpg")?.ToString()), "应返回 TMDb 人物头像。");
                    Assert.IsFalse(images.Any(image => image.Url?.Contains("douban", StringComparison.OrdinalIgnoreCase) == true), "tmdb-only 自动人物图片链路不应再泄漏 Douban 图片 URL。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
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

                var info = new Person
                {
                    Name = "周迅",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "27257290" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动人物图片链路在没有 TMDb fallback 时也不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new PersonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.AreEqual(0, images.Count, "tmdb-only 自动人物图片链路在没有插件内 TMDb fallback 时应直接返回空集合。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
            }
        }

        [TestMethod]
        public void ManualRemoteImageSearchBlocksDoubanUnderTmdbOnly()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;

                var info = new Person
                {
                    Name = "周迅",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "27257290" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = CreateManualRemoteImageContextAccessor();
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 手动人物图片搜索不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new PersonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.AreEqual(0, images.Count, "tmdb-only 下手动人物远程图片搜索不应返回 Douban 图片。 ");
                    Assert.IsFalse(images.Any(image => image.Url?.Contains("douban", StringComparison.OrdinalIgnoreCase) == true), "tmdb-only 下手动人物远程图片搜索不应返回 Douban 图片。 ");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
            }
        }

        [DataTestMethod]
        [DataRow("en-US", 288001)]
        [DataRow("ja-JP", 288002)]
        [DataRow("zh-CN", 288003)]
        public void ManualRemoteImages_TmdbPersonFiltersAllowedLanguagesAndPreservesInputOrder(string preferredLanguage, int tmdbId)
        {
            var info = new Person
            {
                Name = "多语言人物图片",
                PreferredMetadataLanguage = preferredLanguage,
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessor = CreateManualRemoteImageContextAccessor();
            var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "手动 TMDb 人物图片链路不应访问 Douban。");
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            SeedTmdbPersonProfiles(tmdbApi, tmdbId, CreateMultilingualPersonProfiles());
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            Task.Run(async () =>
            {
                var provider = new PersonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                Assert.AreEqual(6, images.Count, "手动人物 RemoteImages 应仅返回中/日/英/null/empty 范围内的 TMDb 头像候选。");
                AssertManualImageLanguagesInOrder(images, "人物头像", "en", null, "ja", string.Empty, "zh", "zh-CN");
                AssertManualImageUrlsPresent(
                    images,
                    "人物头像",
                    tmdbApi.GetProfileUrl("/person-profile-en.jpg")?.ToString(),
                    tmdbApi.GetProfileUrl("/person-profile-no-language.jpg")?.ToString(),
                    tmdbApi.GetProfileUrl("/person-profile-ja.jpg")?.ToString(),
                    tmdbApi.GetProfileUrl("/person-profile-empty-language.jpg")?.ToString(),
                    tmdbApi.GetProfileUrl("/person-profile-zh.jpg")?.ToString(),
                    tmdbApi.GetProfileUrl("/person-profile-zh-cn.jpg")?.ToString());
                Assert.IsFalse(images.Any(image => string.Equals(image.Language, "fr", StringComparison.OrdinalIgnoreCase)), "手动人物 RemoteImages 应过滤法语图片。");
                Assert.IsFalse(images.Any(image => string.Equals(image.Language, "ko", StringComparison.OrdinalIgnoreCase)), "手动人物 RemoteImages 应过滤韩语图片。");
                Assert.IsTrue(images.All(image => image.Type == ImageType.Primary), "人物图片 Provider 只应返回主图类型。");
                Assert.IsTrue(images.All(image => image.ProviderName == MetaSharkPlugin.PluginName), "TMDb 人物图片 provider name 应保持插件名。");
                Assert.IsTrue(images.Any(image => image.Language == null), "手动人物 RemoteImages 应保留 null 无语言图片。");
                Assert.IsTrue(images.Any(image => image.Language == string.Empty), "手动人物 RemoteImages 应保留 empty 无语言图片。");
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

        private static IMemoryCache GetDoubanMemoryCache(DoubanApi doubanApi)
        {
            var memoryCacheField = typeof(DoubanApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "DoubanApi.memoryCache 未定义");

            var memoryCache = memoryCacheField!.GetValue(doubanApi) as IMemoryCache;
            Assert.IsNotNull(memoryCache, "DoubanApi.memoryCache 不是有效的 IMemoryCache");
            return memoryCache!;
        }

        private static void SeedTmdbPersonProfiles(TmdbApi tmdbApi, int tmdbId)
        {
            SeedTmdbPersonProfiles(
                tmdbApi,
                tmdbId,
                new List<ImageData>
                {
                    CreateImageData("/person-profile.jpg", null, 800, 1200),
                });
        }

        private static void SeedTmdbPersonProfiles(TmdbApi tmdbApi, int tmdbId, IList<ImageData> profiles)
        {
            var person = new TmdbPerson
            {
                Id = tmdbId,
                Biography = "TMDb seeded biography",
            };
            SetTmdbImages(person, ("Profiles", profiles));

            GetTmdbMemoryCache(tmdbApi).Set($"person-{tmdbId}", person, TimeSpan.FromMinutes(5));
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

        private static List<ImageData> CreateMultilingualPersonProfiles()
        {
            return new List<ImageData>
            {
                CreateImageData("/person-profile-en.jpg", "en", 800, 1200),
                CreateImageData("/person-profile-fr.jpg", "fr", 800, 1200),
                CreateImageData("/person-profile-ko.jpg", "ko", 800, 1200),
                CreateImageData("/person-profile-no-language.jpg", null, 800, 1200),
                CreateImageData("/person-profile-ja.jpg", "ja", 800, 1200),
                CreateImageData("/person-profile-empty-language.jpg", string.Empty, 800, 1200),
                CreateImageData("/person-profile-zh.jpg", "zh", 800, 1200),
                CreateImageData("/person-profile-zh-cn.jpg", "zh-CN", 800, 1200),
            };
        }

        private static void AssertManualImageLanguagesInOrder(IEnumerable<RemoteImageInfo> images, string imageKind, params string?[] expectedLanguages)
        {
            var actualLanguages = images.Select(image => FormatLanguageValue(image.Language)).ToArray();
            var expectedLanguageValues = expectedLanguages.Select(FormatLanguageValue).ToArray();

            CollectionAssert.AreEqual(
                expectedLanguageValues,
                actualLanguages,
                imageKind + "过滤后应保持 TMDb 输入相对顺序并保留原始语言值，不能做插件侧语言排序。实际语言: " + string.Join(", ", actualLanguages));
        }

        private static void AssertManualImageUrlsPresent(IEnumerable<RemoteImageInfo> images, string imageKind, params string?[] expectedUrls)
        {
            var actualUrls = images.Select(image => image.Url).ToList();
            foreach (var expectedUrl in expectedUrls)
            {
                Assert.IsNotNull(expectedUrl, imageKind + "的候选 URL 不应为空。实际 URL: " + string.Join(", ", actualUrls));
                Assert.IsTrue(actualUrls.Contains(expectedUrl), imageKind + "应保留候选 URL: " + expectedUrl + "。实际 URL: " + string.Join(", ", actualUrls));
            }
        }

        private static string FormatLanguageValue(string? language)
        {
            return language == null ? "<null>" : language.Length == 0 ? "<empty>" : language;
        }

        private static void SeedDoubanCelebrity(DoubanApi doubanApi, DoubanCelebrity celebrity)
        {
            GetDoubanMemoryCache(doubanApi).Set($"personage_{celebrity.Id}", celebrity, TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanCelebrityPhotos(DoubanApi doubanApi, string celebrityId, params DoubanPhoto[] photos)
        {
            GetDoubanMemoryCache(doubanApi).Set($"personage_photo_{celebrityId}", photos.ToList(), TimeSpan.FromMinutes(5));
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
