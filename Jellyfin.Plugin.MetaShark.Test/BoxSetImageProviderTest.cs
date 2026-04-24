using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.General;
using TmdbCollection = TMDbLib.Objects.Collections.Collection;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class BoxSetImageProviderTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.TimestampFormat = "hh:mm:ss ";
            }));

        [DataTestMethod]
        [DataRow("en-US", 770001)]
        [DataRow("ja-JP", 770002)]
        [DataRow("zh-CN", 770003)]
        public void ManualRemoteImages_TmdbBoxSetFiltersAllowedLanguagesAndPreservesInputOrder(string preferredLanguage, int tmdbId)
        {
            var boxSet = new BoxSet
            {
                Name = "多语言合集图片",
                PreferredMetadataLanguage = preferredLanguage,
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                },
            };
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessor = CreateManualRemoteImageContextAccessor();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            var images = CreateMultilingualCollectionImages();
            SeedTmdbCollection(tmdbApi, tmdbId, images.Posters, images.Backdrops);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            Task.Run(async () =>
            {
                var provider = new BoxSetImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                var remoteImages = (await provider.GetImages(boxSet, CancellationToken.None)).ToList();
                var posters = remoteImages.Where(image => image.Type == ImageType.Primary).ToList();
                var backdrops = remoteImages.Where(image => image.Type == ImageType.Backdrop).ToList();

                Assert.AreEqual(12, remoteImages.Count, "手动合集 RemoteImages 应仅返回中/日/英/null/empty 范围内的 TMDb 海报和背景图候选。");
                Assert.AreEqual(6, posters.Count, "手动合集 RemoteImages 应保留 6 张合格海报。");
                Assert.AreEqual(6, backdrops.Count, "手动合集 RemoteImages 应保留 6 张合格背景图。");
                AssertManualImageLanguagesInOrder(posters, "合集海报", "en", null, "ja", string.Empty, "zh", "zh-CN");
                AssertManualImageLanguagesInOrder(backdrops, "合集背景图", "en", null, "ja", string.Empty, "zh", "zh-CN");
                AssertManualImageUrlsPresent(
                    posters,
                    "合集海报",
                    tmdbApi.GetPosterUrl("/boxset-poster-en.jpg")?.ToString(),
                    tmdbApi.GetPosterUrl("/boxset-poster-no-language.jpg")?.ToString(),
                    tmdbApi.GetPosterUrl("/boxset-poster-ja.jpg")?.ToString(),
                    tmdbApi.GetPosterUrl("/boxset-poster-empty-language.jpg")?.ToString(),
                    tmdbApi.GetPosterUrl("/boxset-poster-zh.jpg")?.ToString(),
                    tmdbApi.GetPosterUrl("/boxset-poster-zh-cn.jpg")?.ToString());
                AssertManualImageUrlsPresent(
                    backdrops,
                    "合集背景图",
                    tmdbApi.GetBackdropUrl("/boxset-backdrop-en.jpg")?.ToString(),
                    tmdbApi.GetBackdropUrl("/boxset-backdrop-no-language.jpg")?.ToString(),
                    tmdbApi.GetBackdropUrl("/boxset-backdrop-ja.jpg")?.ToString(),
                    tmdbApi.GetBackdropUrl("/boxset-backdrop-empty-language.jpg")?.ToString(),
                    tmdbApi.GetBackdropUrl("/boxset-backdrop-zh.jpg")?.ToString(),
                    tmdbApi.GetBackdropUrl("/boxset-backdrop-zh-cn.jpg")?.ToString());
                Assert.IsFalse(remoteImages.Any(image => string.Equals(image.Language, "fr", StringComparison.OrdinalIgnoreCase)), "手动合集 RemoteImages 应过滤法语图片。");
                Assert.IsFalse(remoteImages.Any(image => string.Equals(image.Language, "ko", StringComparison.OrdinalIgnoreCase)), "手动合集 RemoteImages 应过滤韩语图片。");
                Assert.IsTrue(remoteImages.All(image => image.ProviderName == MetaSharkPlugin.PluginName), "TMDb 合集图片 provider name 应保持插件名。");
                Assert.IsTrue(remoteImages.Any(image => image.Language == null), "手动合集 RemoteImages 应保留 null 无语言图片。");
                Assert.IsTrue(remoteImages.Any(image => image.Language == string.Empty), "手动合集 RemoteImages 应保留 empty 无语言图片。");
            }).GetAwaiter().GetResult();
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

        private static void SeedTmdbCollection(TmdbApi tmdbApi, int tmdbId, IList<ImageData> posters, IList<ImageData> backdrops)
        {
            var collection = new TmdbCollection
            {
                Id = tmdbId,
                Name = "多语言合集图片",
            };
            SetTmdbImages(collection, ("Posters", posters), ("Backdrops", backdrops));

            GetTmdbMemoryCache(tmdbApi).Set(
                $"collection-{tmdbId}--",
                collection,
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

        private static (List<ImageData> Posters, List<ImageData> Backdrops) CreateMultilingualCollectionImages()
        {
            return (
                new List<ImageData>
                {
                    CreateImageData("/boxset-poster-en.jpg", "en", 1000, 1500),
                    CreateImageData("/boxset-poster-fr.jpg", "fr", 1000, 1500),
                    CreateImageData("/boxset-poster-ko.jpg", "ko", 1000, 1500),
                    CreateImageData("/boxset-poster-no-language.jpg", null, 1000, 1500),
                    CreateImageData("/boxset-poster-ja.jpg", "ja", 1000, 1500),
                    CreateImageData("/boxset-poster-empty-language.jpg", string.Empty, 1000, 1500),
                    CreateImageData("/boxset-poster-zh.jpg", "zh", 1000, 1500),
                    CreateImageData("/boxset-poster-zh-cn.jpg", "zh-CN", 1000, 1500),
                },
                new List<ImageData>
                {
                    CreateImageData("/boxset-backdrop-en.jpg", "en", 1920, 1080),
                    CreateImageData("/boxset-backdrop-fr.jpg", "fr", 1920, 1080),
                    CreateImageData("/boxset-backdrop-ko.jpg", "ko", 1920, 1080),
                    CreateImageData("/boxset-backdrop-no-language.jpg", null, 1920, 1080),
                    CreateImageData("/boxset-backdrop-ja.jpg", "ja", 1920, 1080),
                    CreateImageData("/boxset-backdrop-empty-language.jpg", string.Empty, 1920, 1080),
                    CreateImageData("/boxset-backdrop-zh.jpg", "zh", 1920, 1080),
                    CreateImageData("/boxset-backdrop-zh-cn.jpg", "zh-CN", 1920, 1080),
                });
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
    }
}
