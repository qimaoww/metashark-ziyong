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
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class SeriesImageProviderTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-series-image-provider-tests");
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
            var info = new MediaBrowser.Controller.Entities.TV.Series()
            {
                Name = "花牌情缘",
                PreferredMetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string> { { BaseProvider.DoubanProviderId, "6439459" }, { MetadataProvider.Tmdb.ToString(), "45247" } }
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
                try
                {
                    var provider = new SeriesImageProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var result = await provider.GetImages(info, CancellationToken.None);
                    Assert.IsNotNull(result);

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
        public void TestGetImagesFromTMDB()
        {
            var info = new MediaBrowser.Controller.Entities.TV.Series()
            {
                PreferredMetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), "67534" }, { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() } }
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
                var provider = new SeriesImageProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
                var result = await provider.GetImages(info, CancellationToken.None);
                Assert.IsNotNull(result);

                var str = result.ToJson();
                Console.WriteLine(result.ToJson());
            }).GetAwaiter().GetResult();
        }

        [TestMethod]
        public void TestGetImagesFallsBackToTmdbWhenDoubanBlocked()
        {
            var info = new MediaBrowser.Controller.Entities.TV.Series()
            {
                Name = "花牌情缘",
                PreferredMetadataLanguage = "zh",
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
                    var provider = new SeriesImageProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
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

                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "花牌情缘",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "6439459" },
                        { MetadataProvider.Tmdb.ToString(), "45247" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动剧集图片链路不应再访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeriesDetails(tmdbApi, 45247, "zh");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.IsTrue(images.Any(), "tmdb-only 自动剧集图片链路在存在有效 TMDb id 时应改道到 TMDb。");
                    Assert.IsTrue(images.Any(image => image.Url == tmdbApi.GetPosterUrl("/series-poster.jpg")?.ToString()), "应返回 TMDb 剧集海报。");
                    Assert.IsFalse(images.Any(image => image.Url?.Contains("douban", StringComparison.OrdinalIgnoreCase) == true), "tmdb-only 自动剧集图片链路不应再泄漏 Douban 图片 URL。");
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

                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "只有豆瓣剧照",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, "only-douban-series" },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 自动剧集图片链路在没有 TMDb fallback 时也不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.AreEqual(0, images.Count, "tmdb-only 自动剧集图片链路在没有插件内 TMDb fallback 时应直接返回空集合。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
            }
        }

        [DataTestMethod]
        [DataRow("en-US", 98765)]
        [DataRow("ja-JP", 98769)]
        [DataRow("zh-CN", 98770)]
        public void ManualRemoteImages_TmdbSeriesFiltersAllowedLanguagesAndPreservesLanguageValues(string preferredLanguage, int tmdbId)
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalEnableTmdb = plugin.Configuration.EnableTmdb;

            try
            {
                plugin.Configuration.EnableTmdb = true;

                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "多语言图片剧集",
                    PreferredMetadataLanguage = preferredLanguage,
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                        { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = CreateManualRemoteImageContextAccessor();
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "手动 TMDb 剧集图片测试不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeriesDetails(tmdbApi, tmdbId, preferredLanguage);
                SeedTmdbSeriesImages(tmdbApi, tmdbId, CreateMultilingualSeriesImages());
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();
                    var posters = images.Where(image => image.Type == ImageType.Primary).ToList();
                    var backdrops = images.Where(image => image.Type == ImageType.Backdrop).ToList();
                    var logos = images.Where(image => image.Type == ImageType.Logo).ToList();

                    Assert.AreEqual(6, posters.Count, "手动 RemoteImages 应仅返回中/日/英/无语言范围内的 TMDb 海报候选。");
                    Assert.AreEqual(6, backdrops.Count, "手动 RemoteImages 应仅返回中/日/英/无语言范围内的 TMDb 背景图候选。");
                    Assert.AreEqual(6, logos.Count, "手动 RemoteImages 应仅返回中/日/英/无语言范围内的 TMDb Logo 候选。");
                    AssertManualImageLanguages(posters, "剧集海报", 2);
                    AssertManualImageLanguages(backdrops, "剧集背景图", 2);
                    AssertManualImageLanguages(logos, "剧集 Logo", 2);
                    AssertManualImageUrlsPresent(
                        posters,
                        "剧集海报",
                        tmdbApi.GetPosterUrl("/series-poster.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/series-poster-zh-cn.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/series-poster-ja.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/series-poster-en.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/series-poster-no-language.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/series-poster-empty-language.jpg")?.ToString());
                    AssertManualImageUrlsPresent(
                        backdrops,
                        "剧集背景图",
                        tmdbApi.GetBackdropUrl("/series-backdrop.jpg")?.ToString(),
                        tmdbApi.GetBackdropUrl("/series-backdrop-zh-cn.jpg")?.ToString(),
                        tmdbApi.GetBackdropUrl("/series-backdrop-ja.jpg")?.ToString(),
                        tmdbApi.GetBackdropUrl("/series-backdrop-en.jpg")?.ToString(),
                        tmdbApi.GetBackdropUrl("/series-backdrop-no-language.jpg")?.ToString(),
                        tmdbApi.GetBackdropUrl("/series-backdrop-empty-language.jpg")?.ToString());
                    AssertManualImageUrlsPresent(
                        logos,
                        "剧集 Logo",
                        tmdbApi.GetLogoUrl("/series-logo.png")?.ToString(),
                        tmdbApi.GetLogoUrl("/series-logo-zh-cn.png")?.ToString(),
                        tmdbApi.GetLogoUrl("/series-logo-ja.png")?.ToString(),
                        tmdbApi.GetLogoUrl("/series-logo-en.png")?.ToString(),
                        tmdbApi.GetLogoUrl("/series-logo-no-language.png")?.ToString(),
                        tmdbApi.GetLogoUrl("/series-logo-empty-language.png")?.ToString());
                    AssertManualImageLanguagesInOrder(posters, "剧集海报", "en", null, "ja", string.Empty, "zh", "zh-CN");
                    AssertManualImageLanguagesInOrder(backdrops, "剧集背景图", "en", null, "ja", string.Empty, "zh", "zh-CN");
                    AssertManualImageLanguagesInOrder(logos, "剧集 Logo", "en", null, "ja", string.Empty, "zh", "zh-CN");
                    Assert.IsFalse(images.Any(image => string.Equals(image.Language, "fr", StringComparison.OrdinalIgnoreCase)), "手动 RemoteImages 应过滤法语等其他语言图片。");
                    Assert.IsFalse(images.Any(image => string.Equals(image.Language, "ko", StringComparison.OrdinalIgnoreCase)), "手动 RemoteImages 应过滤韩语等其他语言图片。");
                    Assert.IsTrue(posters.Any(image => image.Url == tmdbApi.GetPosterUrl("/series-poster-en.jpg")?.ToString()), "海报应使用 TMDb poster URL helper。");
                    Assert.IsTrue(backdrops.Any(image => image.Url == tmdbApi.GetBackdropUrl("/series-backdrop-ja.jpg")?.ToString()), "背景图应使用 TMDb backdrop URL helper。");
                    Assert.IsTrue(logos.Any(image => image.Url == tmdbApi.GetLogoUrl("/series-logo-no-language.png")?.ToString()), "Logo 应使用 TMDb logo URL helper。");
                    Assert.IsTrue(images.All(image => image.ProviderName == MetaSharkPlugin.PluginName), "TMDb 图片 provider name 应保持插件名。");
                    Assert.IsTrue(posters.All(image => image.RatingType == RatingType.Score), "海报评分类型应保持 Score。");
                    Assert.IsTrue(posters.Any(image => image.Width == 1000 && image.Height == 1500 && image.CommunityRating == 8.5 && image.VoteCount == 10), "海报应保留评分、尺寸和投票数。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
        }

        [TestMethod]
        public void ManualRemoteImages_TmdbSeriesWithoutChineseKeepsAllowedLanguages()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalEnableTmdb = plugin.Configuration.EnableTmdb;

            try
            {
                plugin.Configuration.EnableTmdb = true;

                var tmdbId = 98768;
                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "无中文图片剧集",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                        { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = CreateManualRemoteImageContextAccessor();
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "手动 TMDb 剧集图片测试不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeriesDetails(tmdbApi, tmdbId, "zh");
                SeedTmdbSeriesImages(tmdbApi, tmdbId, CreateSeriesImagesWithoutChinese());
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();
                    var posters = images.Where(image => image.Type == ImageType.Primary).ToList();
                    var backdrops = images.Where(image => image.Type == ImageType.Backdrop).ToList();
                    var logos = images.Where(image => image.Type == ImageType.Logo).ToList();

                    Assert.AreEqual(4, posters.Count, "无中文候选时，手动 RemoteImages 仍应保留日/英/无语言范围内的 TMDb 海报候选。");
                    Assert.AreEqual(4, backdrops.Count, "无中文候选时，手动 RemoteImages 仍应保留日/英/无语言范围内的 TMDb 背景图候选。");
                    Assert.AreEqual(4, logos.Count, "无中文候选时，手动 RemoteImages 仍应保留日/英/无语言范围内的 TMDb Logo 候选。");
                    AssertManualImageLanguages(posters, "无中文剧集海报", 0);
                    AssertManualImageLanguages(backdrops, "无中文剧集背景图", 0);
                    AssertManualImageLanguages(logos, "无中文剧集 Logo", 0);
                    AssertManualImageUrlsPresent(
                        posters,
                        "无中文剧集海报",
                        tmdbApi.GetPosterUrl("/series-poster-ja.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/series-poster-en.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/series-poster-no-language.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/series-poster-empty-language.jpg")?.ToString());
                    AssertManualImageUrlsPresent(
                        backdrops,
                        "无中文剧集背景图",
                        tmdbApi.GetBackdropUrl("/series-backdrop-ja.jpg")?.ToString(),
                        tmdbApi.GetBackdropUrl("/series-backdrop-en.jpg")?.ToString(),
                        tmdbApi.GetBackdropUrl("/series-backdrop-no-language.jpg")?.ToString(),
                        tmdbApi.GetBackdropUrl("/series-backdrop-empty-language.jpg")?.ToString());
                    AssertManualImageUrlsPresent(
                        logos,
                        "无中文剧集 Logo",
                        tmdbApi.GetLogoUrl("/series-logo-ja.png")?.ToString(),
                        tmdbApi.GetLogoUrl("/series-logo-en.png")?.ToString(),
                        tmdbApi.GetLogoUrl("/series-logo-no-language.png")?.ToString(),
                        tmdbApi.GetLogoUrl("/series-logo-empty-language.png")?.ToString());
                    Assert.IsFalse(images.Any(image => string.Equals(image.Language, "fr", StringComparison.OrdinalIgnoreCase)), "无中文手动 RemoteImages 应过滤法语等其他语言图片。");
                    Assert.IsFalse(images.Any(image => string.Equals(image.Language, "ko", StringComparison.OrdinalIgnoreCase)), "无中文手动 RemoteImages 应过滤韩语等其他语言图片。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
        }

        [TestMethod]
        public void ManualRemoteImages_DoubanSeriesKeepsZhJaEnNoLanguageTmdbCandidates()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalEnableTmdb = plugin.Configuration.EnableTmdb;

            try
            {
                plugin.Configuration.EnableTmdb = true;

                var tmdbId = 98767;
                var sid = "douban-manual-series";
                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "豆瓣来源多语言剧集",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { BaseProvider.DoubanProviderId, sid },
                        { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = CreateManualRemoteImageContextAccessor("series-id");
                var doubanApi = new DoubanApi(this.loggerFactory);
                SeedDoubanSubject(doubanApi, new DoubanSubject
                {
                    Sid = sid,
                    Name = "豆瓣来源多语言剧集",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p98767.jpg",
                    Language = "日语",
                });
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeriesDetails(tmdbApi, tmdbId, "zh");
                SeedTmdbSeriesImages(tmdbApi, tmdbId, CreateMultilingualSeriesImages());
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();
                    var posters = images.Where(image => image.Type == ImageType.Primary).ToList();
                    var tmdbPosters = posters.Where(image => image.Url?.Contains("tmdb", StringComparison.OrdinalIgnoreCase) == true).ToList();

                    Assert.IsTrue(posters.Any(image => image.Url?.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase) == true), "手动 Douban 来源应保留豆瓣中文海报。");
                    var backdrops = images.Where(image => image.Type == ImageType.Backdrop).ToList();
                    var logos = images.Where(image => image.Type == ImageType.Logo).ToList();

                    Assert.AreEqual(7, posters.Count, "手动 Douban 来源应同时追加中/日/英/无语言范围内的 TMDb 海报候选。");
                    AssertManualImageLanguages(tmdbPosters, "Douban 剧集 TMDb 海报", 2);
                    AssertManualImageLanguages(backdrops, "Douban 剧集背景图", 2);
                    AssertManualImageLanguages(logos, "Douban 剧集 Logo", 2);
                    AssertManualImageFileNamesPresent(
                        tmdbPosters,
                        "Douban 剧集 TMDb 海报",
                        "series-poster.jpg",
                        "series-poster-zh-cn.jpg",
                        "series-poster-ja.jpg",
                        "series-poster-en.jpg",
                        "series-poster-no-language.jpg",
                        "series-poster-empty-language.jpg");
                    AssertManualImageFileNamesPresent(
                        backdrops,
                        "Douban 剧集背景图",
                        "series-backdrop.jpg",
                        "series-backdrop-zh-cn.jpg",
                        "series-backdrop-ja.jpg",
                        "series-backdrop-en.jpg",
                        "series-backdrop-no-language.jpg",
                        "series-backdrop-empty-language.jpg");
                    AssertManualImageFileNamesPresent(
                        logos,
                        "Douban 剧集 Logo",
                        "series-logo.png",
                        "series-logo-zh-cn.png",
                        "series-logo-ja.png",
                        "series-logo-en.png",
                        "series-logo-no-language.png",
                        "series-logo-empty-language.png");
                    Assert.IsFalse(images.Any(image => string.Equals(image.Language, "fr", StringComparison.OrdinalIgnoreCase)), "手动 Douban 来源追加 TMDb 候选时应过滤法语等其他语言图片。");
                    Assert.IsFalse(images.Any(image => string.Equals(image.Language, "ko", StringComparison.OrdinalIgnoreCase)), "手动 Douban 来源追加 TMDb 候选时应过滤韩语等其他语言图片。");
                    Assert.IsFalse(images.Any(image => image.Url?.Contains("-fr.", StringComparison.OrdinalIgnoreCase) == true), "手动 Douban 来源追加 TMDb 候选时不应保留法语图片 URL。");
                    Assert.IsFalse(images.Any(image => image.Url?.Contains("-ko.", StringComparison.OrdinalIgnoreCase) == true), "手动 Douban 来源追加 TMDb 候选时不应保留韩语图片 URL。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
            }
        }

        [TestMethod]
        public void AutomaticTmdbSeriesImages_ReturnSelectedPosterAndBackdropOnly()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalEnableTmdb = plugin.Configuration.EnableTmdb;

            try
            {
                plugin.Configuration.EnableTmdb = true;

                var tmdbId = 98766;
                var info = new MediaBrowser.Controller.Entities.TV.Series()
                {
                    Name = "自动图片剧集",
                    PreferredMetadataLanguage = "zh",
                    ProviderIds = new Dictionary<string, string>
                    {
                        { MetadataProvider.Tmdb.ToString(), tmdbId.ToString() },
                        { MetaSharkPlugin.ProviderId, MetaSource.Tmdb.ToString() },
                    },
                };
                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "自动 TMDb 剧集图片测试不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeriesDetails(tmdbApi, tmdbId, "zh");
                SeedTmdbSeriesImages(tmdbApi, tmdbId, CreateMultilingualSeriesImages());
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                Task.Run(async () =>
                {
                    var provider = new SeriesImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();
                    var posters = images.Where(image => image.Type == ImageType.Primary).ToList();
                    var backdrops = images.Where(image => image.Type == ImageType.Backdrop).ToList();

                    Assert.AreEqual(1, posters.Count, "自动刷新应继续只返回当前选中的 TMDb 海报。");
                    Assert.AreEqual(1, backdrops.Count, "自动刷新应继续只返回当前选中的 TMDb 背景图。");
                    Assert.AreEqual(tmdbApi.GetPosterUrl("/series-poster.jpg")?.ToString(), posters[0].Url);
                    Assert.AreEqual(tmdbApi.GetBackdropUrl("/series-backdrop.jpg")?.ToString(), backdrops[0].Url);
                    Assert.AreEqual("zh", posters[0].Language, "自动刷新海报语言应保持首选语言。");
                    Assert.AreEqual("zh", backdrops[0].Language, "自动刷新背景图语言应保持首选语言。");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                plugin.Configuration.EnableTmdb = originalEnableTmdb;
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

        private static void SeedTmdbSeriesDetails(TmdbApi tmdbApi, int tmdbId, string language)
        {
            var cache = GetTmdbMemoryCache(tmdbApi);
            cache.Set(
                $"series-{tmdbId}-{language}-{language}",
                new TvShow
                {
                    Id = tmdbId,
                    Name = "花牌情缘",
                    OriginalName = "ちはやふる",
                    PosterPath = "/series-poster.jpg",
                    BackdropPath = "/series-backdrop.jpg",
                },
                TimeSpan.FromMinutes(5));
            cache.Set(
                $"series-images-{tmdbId}--",
                new ImagesWithId
                {
                    Posters = new List<ImageData> { CreateImageData("/series-poster.jpg", "zh", 1000, 1500) },
                    Backdrops = new List<ImageData> { CreateImageData("/series-backdrop.jpg", "zh", 1920, 1080) },
                    Logos = new List<ImageData> { CreateImageData("/series-logo.png", "zh", 500, 250) },
                },
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbSeriesImages(TmdbApi tmdbApi, int tmdbId, ImagesWithId images)
        {
            var cache = GetTmdbMemoryCache(tmdbApi);
            cache.Set(
                $"series-images-{tmdbId}--",
                images,
                TimeSpan.FromMinutes(5));
        }

        private static ImagesWithId CreateMultilingualSeriesImages()
        {
            return new ImagesWithId
            {
                Posters = new List<ImageData>
                {
                    CreateImageData("/series-poster-en.jpg", "en", 1000, 1500),
                    CreateImageData("/series-poster-fr.jpg", "fr", 1000, 1500),
                    CreateImageData("/series-poster-ko.jpg", "ko", 1000, 1500),
                    CreateImageData("/series-poster-no-language.jpg", null, 1000, 1500),
                    CreateImageData("/series-poster-ja.jpg", "ja", 1000, 1500),
                    CreateImageData("/series-poster-empty-language.jpg", string.Empty, 1000, 1500),
                    CreateImageData("/series-poster.jpg", "zh", 1000, 1500),
                    CreateImageData("/series-poster-zh-cn.jpg", "zh-CN", 1000, 1500),
                },
                Backdrops = new List<ImageData>
                {
                    CreateImageData("/series-backdrop-en.jpg", "en", 1920, 1080),
                    CreateImageData("/series-backdrop-fr.jpg", "fr", 1920, 1080),
                    CreateImageData("/series-backdrop-ko.jpg", "ko", 1920, 1080),
                    CreateImageData("/series-backdrop-no-language.jpg", null, 1920, 1080),
                    CreateImageData("/series-backdrop-ja.jpg", "ja", 1920, 1080),
                    CreateImageData("/series-backdrop-empty-language.jpg", string.Empty, 1920, 1080),
                    CreateImageData("/series-backdrop.jpg", "zh", 1920, 1080),
                    CreateImageData("/series-backdrop-zh-cn.jpg", "zh-CN", 1920, 1080),
                },
                Logos = new List<ImageData>
                {
                    CreateImageData("/series-logo-en.png", "en", 500, 250),
                    CreateImageData("/series-logo-fr.png", "fr", 500, 250),
                    CreateImageData("/series-logo-ko.png", "ko", 500, 250),
                    CreateImageData("/series-logo-no-language.png", null, 500, 250),
                    CreateImageData("/series-logo-ja.png", "ja", 500, 250),
                    CreateImageData("/series-logo-empty-language.png", string.Empty, 500, 250),
                    CreateImageData("/series-logo.png", "zh", 500, 250),
                    CreateImageData("/series-logo-zh-cn.png", "zh-CN", 500, 250),
                },
            };
        }

        private static ImagesWithId CreateSeriesImagesWithoutChinese()
        {
            return new ImagesWithId
            {
                Posters = new List<ImageData>
                {
                    CreateImageData("/series-poster-en.jpg", "en", 1000, 1500),
                    CreateImageData("/series-poster-fr.jpg", "fr", 1000, 1500),
                    CreateImageData("/series-poster-ko.jpg", "ko", 1000, 1500),
                    CreateImageData("/series-poster-no-language.jpg", null, 1000, 1500),
                    CreateImageData("/series-poster-ja.jpg", "ja", 1000, 1500),
                    CreateImageData("/series-poster-empty-language.jpg", string.Empty, 1000, 1500),
                },
                Backdrops = new List<ImageData>
                {
                    CreateImageData("/series-backdrop-en.jpg", "en", 1920, 1080),
                    CreateImageData("/series-backdrop-fr.jpg", "fr", 1920, 1080),
                    CreateImageData("/series-backdrop-ko.jpg", "ko", 1920, 1080),
                    CreateImageData("/series-backdrop-no-language.jpg", null, 1920, 1080),
                    CreateImageData("/series-backdrop-ja.jpg", "ja", 1920, 1080),
                    CreateImageData("/series-backdrop-empty-language.jpg", string.Empty, 1920, 1080),
                },
                Logos = new List<ImageData>
                {
                    CreateImageData("/series-logo-en.png", "en", 500, 250),
                    CreateImageData("/series-logo-fr.png", "fr", 500, 250),
                    CreateImageData("/series-logo-ko.png", "ko", 500, 250),
                    CreateImageData("/series-logo-no-language.png", null, 500, 250),
                    CreateImageData("/series-logo-ja.png", "ja", 500, 250),
                    CreateImageData("/series-logo-empty-language.png", string.Empty, 500, 250),
                },
            };
        }

        private static void AssertManualImageLanguages(IEnumerable<RemoteImageInfo> images, string imageKind, int expectedChineseCount)
        {
            var imageList = images.ToList();
            Assert.AreEqual(expectedChineseCount + 4, imageList.Count, imageKind + "应只保留中文、日语、英语和 null/empty 无语言图片。实际语言: " + FormatLanguages(imageList));
            Assert.IsTrue(imageList.All(image => IsChineseLanguage(image.Language) || string.Equals(image.Language, "ja", StringComparison.OrdinalIgnoreCase) || string.Equals(image.Language, "en", StringComparison.OrdinalIgnoreCase) || IsNoLanguage(image.Language)), imageKind + "应仅保留中文、日语、英语和 null/empty 无语言图片。实际语言: " + FormatLanguages(imageList));
            Assert.AreEqual(expectedChineseCount, imageList.Count(image => IsChineseLanguage(image.Language)), imageKind + "中文图片数量应保持不变。实际语言: " + FormatLanguages(imageList));
            Assert.IsFalse(imageList.Any(image => string.Equals(image.Language, "fr", StringComparison.OrdinalIgnoreCase)), imageKind + "不应保留其他语言图片。实际语言: " + FormatLanguages(imageList));
            Assert.IsFalse(imageList.Any(image => string.Equals(image.Language, "ko", StringComparison.OrdinalIgnoreCase)), imageKind + "不应保留其他语言图片。实际语言: " + FormatLanguages(imageList));

            if (expectedChineseCount > 0)
            {
                Assert.IsTrue(imageList.Any(image => string.Equals(image.Language, "zh", StringComparison.OrdinalIgnoreCase)), imageKind + "应保留 zh 中文图片。");
                Assert.IsTrue(imageList.Any(image => string.Equals(image.Language, "zh-CN", StringComparison.OrdinalIgnoreCase)), imageKind + "应保留 zh-CN 中文图片。");
            }

            Assert.IsTrue(imageList.Any(image => string.Equals(image.Language, "ja", StringComparison.OrdinalIgnoreCase)), imageKind + "应保留日语图片。");
            Assert.IsTrue(imageList.Any(image => string.Equals(image.Language, "en", StringComparison.OrdinalIgnoreCase)), imageKind + "应保留英语图片。");
            Assert.IsTrue(imageList.Any(image => image.Language == null), imageKind + "应保留 null 无语言图片。");
            Assert.IsTrue(imageList.Any(image => image.Language == string.Empty), imageKind + "应保留 empty 无语言图片。");
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

        private static void AssertManualImageFileNamesPresent(IEnumerable<RemoteImageInfo> images, string imageKind, params string[] expectedFileNames)
        {
            var urls = images.Select(image => image.Url ?? string.Empty).ToList();
            foreach (var expectedFileName in expectedFileNames)
            {
                Assert.IsTrue(urls.Any(url => url.Contains(expectedFileName, StringComparison.OrdinalIgnoreCase)), imageKind + "应保留候选图片 " + expectedFileName + "。实际 URL: " + string.Join(", ", urls));
            }
        }

        private static bool IsChineseLanguage(string? language)
        {
            return language != null && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNoLanguage(string? language)
        {
            return string.IsNullOrEmpty(language);
        }

        private static string FormatLanguages(IEnumerable<RemoteImageInfo> images)
        {
            return string.Join(", ", images.Select(image => FormatLanguageValue(image.Language)));
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
            context.Request.QueryString = new QueryString("?includeAllLanguages=true");
            return new HttpContextAccessor
            {
                HttpContext = context,
            };
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
