using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeProviderOverviewGuardTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestMethod]
        public async Task GetMetadata_ShouldPersistOverview_WhenDetailsOverviewWasFetchedWithExplicitZhCn()
        {
            var info = CreateEpisodeInfo(metadataLanguage: "zh-CN");
            var libraryManagerStub = new Mock<ILibraryManager>();
            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "Episode 1",
                Overview = "A reunion episode.",
            });

            using var provider = CreateProvider(libraryManagerStub.Object, new HttpContextAccessor(), tmdbApi);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("A reunion episode.", result.Item!.Overview, "details overview 以显式 zh-CN 请求获取时，应被包装成可信来源并允许写回。");
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldPersistOverview_WhenTranslationOverviewHasExplicitZhCnSource()
        {
            var info = CreateEpisodeInfo(metadataLanguage: "zh-CN");
            var libraryManagerStub = new Mock<ILibraryManager>();
            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "Episode 1",
                Overview = "A reunion episode.",
            });
            SeedEpisodeTranslationOverview(tmdbApi, 123, 1, 1, "zh-CN", "這一集講述兩個年輕人為了一輛車展開較量。");

            using var provider = CreateProvider(libraryManagerStub.Object, new HttpContextAccessor(), tmdbApi);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("這一集講述兩個年輕人為了一輛車展開較量。", result.Item!.Overview);
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldPersistOverview_WhenLookupLanguageIsBareZhButTargetSourceIsPromotedToZhCn()
        {
            var info = CreateEpisodeInfo(metadataLanguage: "zh");
            var libraryManagerStub = new Mock<ILibraryManager>();
            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "Episode 1",
                Overview = "A reunion episode.",
            });
            SeedEpisodeTranslationOverview(tmdbApi, 123, 1, 1, "zh-CN", "這一集講述兩個年輕人為了一輛車展開較量。");

            using var provider = CreateProvider(libraryManagerStub.Object, new HttpContextAccessor(), tmdbApi);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("這一集講述兩個年輕人為了一輛車展開較量。", result.Item!.Overview, "bare zh 请求应先提升到 zh-CN 目标来源，再命中可信的 Episode overview。 ");
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        [TestMethod]
        public void GetMetadata_ShouldKeepOverviewNull_WhenEpisodeDetailsAndTranslationsAreEmptyEvenIfParentsHaveOverview()
        {
            var info = CreateEpisodeInfo(metadataLanguage: "zh-CN");
            var seasonPath = Path.GetDirectoryName(info.Path);
            Assert.IsNotNull(seasonPath);
            var seriesPath = Path.GetDirectoryName(seasonPath);
            Assert.IsNotNull(seriesPath);

            var episodeItem = new Episode
            {
                Path = info.Path,
            };
            var seasonItem = new Season
            {
                Path = seasonPath,
                Overview = "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。",
            };
            var seriesItem = new Series
            {
                Id = Guid.NewGuid(),
                Path = seriesPath,
                Name = "神之水滴",
                Overview = "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);
            libraryManagerStub
                .Setup(x => x.FindByPath(seasonPath!, true))
                .Returns(seasonItem);
            libraryManagerStub
                .Setup(x => x.FindByPath(seriesPath!, true))
                .Returns(seriesItem);
            SetSeries(episodeItem, libraryManagerStub, seriesItem);

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "Pilot",
                Overview = null,
            });
            SeedEpisodeTranslationOverview(tmdbApi, 123, 1, 1, "zh-CN", "   ");

            using var provider = CreateProvider(libraryManagerStub.Object, new HttpContextAccessor(), tmdbApi);

            MetadataResult<Episode>? result = null;
            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                result = provider.GetMetadata(info, CancellationToken.None).GetAwaiter().GetResult();
            });

            Assert.IsNotNull(result);
            Assert.IsNotNull(result!.Item);
            Assert.AreEqual(null, result.Item!.Overview, "当 episode details/translation 都为空时，即便 series 与 season 已有 overview，也不应把父级 synopsis 写回当前剧集。 ");
        }

        [TestMethod]
        public void GetMetadata_ShouldRejectSeriesSynopsis_WhenSeriesNavigationIsMissingButSeriesOverviewCanBeResolved()
        {
            var info = CreateEpisodeInfo(metadataLanguage: "zh-CN");
            var seasonPath = Path.GetDirectoryName(info.Path);
            Assert.IsNotNull(seasonPath);
            var seriesPath = Path.GetDirectoryName(seasonPath);
            Assert.IsNotNull(seriesPath);

            var seriesOverview = "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。";
            var episodeItem = new Episode
            {
                Path = info.Path,
            };
            var seasonItem = new Season
            {
                Path = seasonPath,
                Overview = "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。",
            };
            var seriesItem = new Series
            {
                Id = Guid.NewGuid(),
                Path = seriesPath,
                Name = "神之水滴",
                Overview = seriesOverview,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);
            libraryManagerStub
                .Setup(x => x.FindByPath(seasonPath!, true))
                .Returns(seasonItem);
            libraryManagerStub
                .Setup(x => x.FindByPath(seriesPath!, true))
                .Returns(seriesItem);

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "Pilot",
                Overview = seriesOverview,
            });
            SeedEpisodeTranslationOverview(tmdbApi, 123, 1, 1, "zh-CN", "   ");

            using var provider = CreateProvider(libraryManagerStub.Object, new HttpContextAccessor(), tmdbApi);

            var result = provider.GetMetadata(info, CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsNotNull(result.Item);
            Assert.AreEqual(null, result.Item!.Overview, "即使 episodeItem.Series 导航为空，只要父级路径仍能解析出同文案的 series overview，也必须拒绝把 series synopsis 当成当前剧集简介写回。 ");
        }

        [TestMethod]
        public void ShouldKeepOverview_WhenCanonicalizedSourceLanguageBecomesStrictZhCn()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("ZH-cn", "A reunion episode.", null, null);

            Assert.AreEqual("A reunion episode.", result.Overview);
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenOnlyBareZhSourceLanguageExistsWithoutExplicitOverviewSourceLanguage()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh", "这一集讲述两个年轻人为了一辆车展开较量。", null, null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenOverviewSourceLanguageIsMissing()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(null, "这一集讲述两个年轻人为了一辆车展开较量。", null, null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [DataTestMethod]
        [DataRow("zh-TW")]
        [DataRow("zh-Hans")]
        [DataRow("zh_cn")]
        [DataRow("en")]
        public void ShouldRejectOverview_WhenOverviewSourceLanguageIsNotStrictZhCn(string language)
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(language, "A reunion episode.", null, null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepOverview_WhenSourceLanguageIsZhCnAndOverviewUsesTraditionalCharacters()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "這一集講述兩個年輕人為了一輛車展開較量。", null, null);

            Assert.AreEqual("這一集講述兩個年輕人為了一輛車展開較量。", result.Overview);
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepOverview_WhenSourceLanguageIsZhCnAndOverviewUsesSharedGlyphs()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "千里之外", null, null);

            Assert.AreEqual("千里之外", result.Overview);
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepOverview_WhenTrustedZhCnSourceUsesMixedText()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "第1集 Reunion", null, null);

            Assert.AreEqual("第1集 Reunion", result.Overview);
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldReturnNullPair_WhenOverviewIsNullOrWhitespace()
        {
            var nullResult = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", null, null, null);
            var whitespaceResult = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "   ", null, null);

            Assert.AreEqual(null, nullResult.Overview);
            Assert.AreEqual(null, nullResult.ResultLanguage);
            Assert.AreEqual(null, whitespaceResult.Overview);
            Assert.AreEqual(null, whitespaceResult.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenEpisodeOverviewEqualsSeriesOverviewAfterSourceAcceptance()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenEpisodeOverviewEqualsSeasonOverviewAfterSourceAcceptance()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。",
                null,
                "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。");

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ResolveEpisodeOverviewPersistence_ShouldRejectOverviewMatchingSeriesOverview_WhenSeasonOverviewDiffers()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。");

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenEpisodeOverviewMatchesParentOverviewAfterWhitespaceNormalization()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "世界级的葡萄酒评论家神咲丰多香辞世后，\n留下了一批葡萄酒收藏。",
                "  世界级的葡萄酒评论家神咲丰多香辞世后， 留下了一批葡萄酒收藏。  ",
                null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenEpisodeOverviewIsHighlySimilarToSeriesOverviewAfterSourceAcceptance()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏！",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepOverview_WhenEpisodeOverviewDiffersFromParentOverviewsAfterSourceAcceptance()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "雫第一次参加神之水滴选拔挑战。",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。");

            Assert.AreEqual("雫第一次参加神之水滴选拔挑战。", result.Overview);
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        private static EpisodeProvider CreateProvider(ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, TmdbApi tmdbApi, ILoggerFactory? loggerFactory = null)
        {
            loggerFactory ??= LoggerFactory.Create(builder => { });
            return new EpisodeProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager,
                httpContextAccessor,
                new DoubanApi(loggerFactory),
                tmdbApi,
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new TvdbApi(loggerFactory));
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

        private static void SetSeries(Episode episode, Mock<ILibraryManager> libraryManagerStub, Series series)
        {
            libraryManagerStub.Setup(x => x.GetItemById(series.Id)).Returns((BaseItem)series);
            episode.SeriesId = series.Id;
            episode.SeriesName = series.Name ?? string.Empty;
        }

        private static EpisodeInfo CreateEpisodeInfo(string? metadataLanguage = "zh-CN")
        {
            return new EpisodeInfo
            {
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
                MetadataLanguage = metadataLanguage,
                ParentIndexNumber = 1,
                IndexNumber = 1,
                SeriesDisplayOrder = string.Empty,
                SeriesProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "123",
                },
            };
        }

        private static void SeedEpisode(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, string imageLanguages, TvEpisode episode)
        {
            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField, "TmdbApi.memoryCache 未找到");

            var cache = cacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(cache, "TmdbApi.memoryCache 不是有效的 MemoryCache");

            var key = $"episode-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}-{imageLanguages}";
            cache!.Set(key, episode);
        }

        private static void SeedEpisodeTranslationOverview(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, string? overview)
        {
            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField, "TmdbApi.memoryCache 未找到");

            var cache = cacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(cache, "TmdbApi.memoryCache 不是有效的 MemoryCache");

            var key = $"episode-translation-overview-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}";
            cache!.Set(
                key,
                overview == null
                    ? null
                    : new EpisodeLocalizedValue
                    {
                        Value = overview,
                        SourceLanguage = language,
                    });
        }
    }
}
