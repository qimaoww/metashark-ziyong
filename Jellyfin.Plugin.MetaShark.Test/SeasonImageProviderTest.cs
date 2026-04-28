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
        public void ManualIdentifyApplyMetadata_WithTmdbSelected_ReturnsTmdbSeasonImage()
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

                const string manualApplyItemId = "season-1";

                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var info = CreateSeason(libraryManagerStub, "第1季", 1, "season-douban", "series-douban", "34860");
                var httpContextAccessor = CreateManualMatchContextAccessor(manualApplyItemId);
                Assert.IsNotNull(httpContextAccessor.HttpContext, "测试前提失败：必须构造手动 Apply 的 HttpContext。 ");
                Assert.AreEqual(HttpMethods.Post, httpContextAccessor.HttpContext!.Request.Method, "测试前提失败：必须走 POST /Items/RemoteSearch/Apply/{id}。 ");
                Assert.AreEqual($"/Items/RemoteSearch/Apply/{manualApplyItemId}", httpContextAccessor.HttpContext.Request.Path.Value, "测试前提失败：必须模拟 series 手动 Identify/Apply 的请求路径。 ");
                Assert.AreEqual("season-douban", info.GetProviderId(BaseProvider.DoubanProviderId), "测试前提失败：season 必须残留旧 DoubanId，才能覆盖当前错误分支。 ");

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
                var expectedTmdbUrl = tmdbApi.GetPosterUrl("/season-poster.jpg")?.ToString();
                Assert.IsFalse(string.IsNullOrEmpty(expectedTmdbUrl), "测试前提失败：必须成功种入 TMDb season 图。 ");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                WithLibraryManager(libraryManagerStub.Object, () =>
                {
                    var parentSeries = info.Series;
                    Assert.IsNotNull(parentSeries, "测试前提失败：season 必须能解析到 parent series。 ");
                    parentSeries!.SetProviderId(MetaSharkPlugin.ProviderId, "Tmdb_34860");
                    Assert.AreEqual("Tmdb_34860", parentSeries.GetProviderId(MetaSharkPlugin.ProviderId), "测试前提失败：parent series 来源必须显式模拟成手动 Apply 选中的 Tmdb_xxx。 ");

                    Task.Run(async () =>
                    {
                        var provider = new SeasonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                        Assert.AreEqual(1, images.Count, "series 手动 Identify/Apply 元数据后的 follow-up season image 路径应返回唯一的季主图。 ");
                        Assert.AreEqual(expectedTmdbUrl, images[0].Url, "series 手动 Identify/Apply 明确选了 TMDb 时，season image 必须跟随 TMDb，而不是因为残留的 season DoubanId 落回 Douban。\n实际 URL: " + images[0].Url);
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
        public void ManualIdentifyApplyMetadata_TmdbOnlyBlocksDoubanSeasonImage()
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

                const string manualApplyItemId = "season-1";

                var httpClientFactory = new DefaultHttpClientFactory();
                var libraryManagerStub = new Mock<ILibraryManager>();
                var info = CreateSeason(libraryManagerStub, "第1季", 1, "season-douban", "series-douban", "34860");
                var httpContextAccessor = CreateManualMatchContextAccessor(manualApplyItemId);
                Assert.IsNotNull(httpContextAccessor.HttpContext, "测试前提失败：必须构造手动 Apply 的 HttpContext。 ");
                Assert.AreEqual(HttpMethods.Post, httpContextAccessor.HttpContext!.Request.Method, "测试前提失败：必须走 POST /Items/RemoteSearch/Apply/{id}。 ");
                Assert.AreEqual($"/Items/RemoteSearch/Apply/{manualApplyItemId}", httpContextAccessor.HttpContext.Request.Path.Value, "测试前提失败：必须模拟 series 手动 Identify/Apply 的请求路径。 ");
                Assert.AreEqual("season-douban", info.GetProviderId(BaseProvider.DoubanProviderId), "测试前提失败：season 必须保留 DoubanId，才能验证 tmdb-only 不被残留 DoubanId 绕过。 ");

                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 手动季图片 follow-up 不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeasonImages(tmdbApi, 34860, 1, string.Empty, "第1季");
                var expectedTmdbUrl = tmdbApi.GetPosterUrl("/season-poster.jpg")?.ToString();
                Assert.IsFalse(string.IsNullOrEmpty(expectedTmdbUrl), "测试前提失败：必须成功种入 TMDb season 图，才能证明 tmdb-only 可回退到 TMDb。 ");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                WithLibraryManager(libraryManagerStub.Object, () =>
                {
                    var parentSeries = info.Series;
                    Assert.IsNotNull(parentSeries, "测试前提失败：season 必须能解析到 parent series。 ");
                    parentSeries!.SetProviderId(MetaSharkPlugin.ProviderId, "Douban_series-douban");
                    Assert.AreEqual("Douban_series-douban", parentSeries.GetProviderId(MetaSharkPlugin.ProviderId), "测试前提失败：parent series 来源必须显式模拟成手动 Apply 选中的 Douban_xxx。 ");

                    Task.Run(async () =>
                    {
                        var provider = new SeasonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                        Assert.AreEqual(1, images.Count, "tmdb-only 下 series 手动 Identify/Apply 的 follow-up season image 路径应返回唯一的 TMDb 季主图。 ");
                        Assert.AreEqual(expectedTmdbUrl, images[0].Url, "tmdb-only 下即使手动 Apply 选过 Douban，season image 也不应访问或返回 Douban 图。\n实际 URL: " + images[0].Url);
                        Assert.IsFalse(images[0].Url?.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase) == true, "tmdb-only 下手动季图 follow-up 不应返回 Douban 图片。 ");
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

        [DataTestMethod]
        [DataRow("en-US", 34861)]
        [DataRow("ja-JP", 34862)]
        [DataRow("zh-CN", 34863)]
        public void ManualRemoteImages_TmdbSeasonFiltersAllowedLanguagesAndPreservesInputOrder(string preferredLanguage, int seriesTmdbId)
        {
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var info = CreateSeason(libraryManagerStub, "第1季", 1, "season-douban", "series-douban", seriesTmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), MetaSource.Tmdb);
            info.ProviderIds.Remove(BaseProvider.DoubanProviderId);
            info.PreferredMetadataLanguage = preferredLanguage;
            var httpContextAccessor = CreateManualRemoteImageContextAccessor();
            var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "手动 TMDb 季图片链路不应访问 Douban。");
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            SeedTmdbSeasonImages(tmdbApi, seriesTmdbId, 1, string.Empty, "第1季", CreateMultilingualSeasonPosters());
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                Task.Run(async () =>
                {
                    var provider = new SeasonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                    var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                    Assert.AreEqual(6, images.Count, "手动季 RemoteImages 应仅返回中/日/英/null/empty 范围内的 TMDb 海报候选。");
                    AssertManualImageLanguagesInOrder(images, "季海报", "en", null, "ja", string.Empty, "zh", "zh-CN");
                    AssertManualImageUrlsPresent(
                        images,
                        "季海报",
                        tmdbApi.GetPosterUrl("/season-poster-en.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/season-poster-no-language.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/season-poster-ja.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/season-poster-empty-language.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/season-poster-zh.jpg")?.ToString(),
                        tmdbApi.GetPosterUrl("/season-poster-zh-cn.jpg")?.ToString());
                    Assert.IsFalse(images.Any(image => string.Equals(image.Language, "fr", StringComparison.OrdinalIgnoreCase)), "手动季 RemoteImages 应过滤法语图片。");
                    Assert.IsFalse(images.Any(image => string.Equals(image.Language, "ko", StringComparison.OrdinalIgnoreCase)), "手动季 RemoteImages 应过滤韩语图片。");
                    Assert.IsTrue(images.All(image => image.Type == ImageType.Primary), "季图片 Provider 只应返回海报类型。");
                    Assert.IsTrue(images.All(image => image.ProviderName == MetaSharkPlugin.PluginName), "TMDb 季图片 provider name 应保持插件名。");
                    Assert.IsTrue(images.Any(image => image.Language == null), "手动季 RemoteImages 应保留 null 无语言图片。");
                    Assert.IsTrue(images.Any(image => image.Language == string.Empty), "手动季 RemoteImages 应保留 empty 无语言图片。");
                }).GetAwaiter().GetResult();
            });
        }

        [TestMethod]
        public void ManualRemoteImageSearch_TmdbOnly_ReturnsTmdbSeasonImage()
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
                var doubanApi = CreateThrowingDoubanApi(this.loggerFactory, "tmdb-only 手动季远程图片搜索不应访问 Douban。");
                var tmdbApi = new TmdbApi(this.loggerFactory);
                ConfigureTmdbImageConfig(tmdbApi);
                SeedTmdbSeasonImages(tmdbApi, 34860, 1, string.Empty, "第1季");
                var expectedTmdbUrl = tmdbApi.GetPosterUrl("/season-poster.jpg")?.ToString();
                Assert.IsFalse(string.IsNullOrEmpty(expectedTmdbUrl), "测试前提失败：必须成功种入 TMDb season 图。 ");
                var omdbApi = new OmdbApi(this.loggerFactory);
                var imdbApi = new ImdbApi(this.loggerFactory);

                WithLibraryManager(libraryManagerStub.Object, () =>
                {
                    Task.Run(async () =>
                    {
                        var provider = new SeasonImageProvider(httpClientFactory, this.loggerFactory, libraryManagerStub.Object, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi);
                        var images = (await provider.GetImages(info, CancellationToken.None)).ToList();

                        Assert.AreEqual(1, images.Count, "tmdb-only 手动 RemoteImages 搜索路径应返回唯一的 TMDb 季主图。");
                        Assert.AreEqual(expectedTmdbUrl, images[0].Url, "tmdb-only 下的手动季图搜索不应返回 Douban URL。\n实际 URL: " + images[0].Url);
                        Assert.IsFalse(images[0].Url?.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase) == true, "tmdb-only 下手动季图搜索不应返回 Douban 图片。 ");
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
                series.SetProviderId(BaseProvider.MetaSharkTmdbProviderId, seriesTmdbId);
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
            SeedTmdbSeasonImages(
                tmdbApi,
                seriesTmdbId,
                seasonNumber,
                language,
                seasonName,
                new List<ImageData>
                {
                    CreateImageData("/season-poster.jpg", "zh", 1000, 1500),
                });
        }

        private static void SeedTmdbSeasonImages(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, string language, string seasonName, IList<ImageData> posters)
        {
            var season = new TvSeason
            {
                Name = seasonName,
                Overview = "TMDb seeded season overview",
                AirDate = new DateTime(2015, 2, 1),
            };
            SetTmdbImages(season, ("Posters", posters));

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

        private static List<ImageData> CreateMultilingualSeasonPosters()
        {
            return new List<ImageData>
            {
                CreateImageData("/season-poster-en.jpg", "en", 1000, 1500),
                CreateImageData("/season-poster-fr.jpg", "fr", 1000, 1500),
                CreateImageData("/season-poster-ko.jpg", "ko", 1000, 1500),
                CreateImageData("/season-poster-no-language.jpg", null, 1000, 1500),
                CreateImageData("/season-poster-ja.jpg", "ja", 1000, 1500),
                CreateImageData("/season-poster-empty-language.jpg", string.Empty, 1000, 1500),
                CreateImageData("/season-poster-zh.jpg", "zh", 1000, 1500),
                CreateImageData("/season-poster-zh-cn.jpg", "zh-CN", 1000, 1500),
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
    }
}
