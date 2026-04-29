using System.Collections;
using System.Reflection;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
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
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenInfoRepresentsUserRefresh()
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
            Assert.IsNull(store.Peek(currentSeries.Id), "UserRefresh 不应保存 overwrite candidate。 ");
        }


        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenDoubanMetadataResolvesTmdbPeopleDuringUserRefresh()
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
            SeedTmdbPerson(tmdbApi, 5101, "剧集演员中文名", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 5102, string.Empty, language: "zh-CN");
            SeedTmdbPersonTranslations(tmdbApi, 5102);
            SeedTmdbPerson(tmdbApi, 5201, "剧集导演中文名", language: "zh-CN");
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
            Assert.AreEqual(1, result.People?.Count ?? 0, "UserRefresh 下 candidate 计数仍应基于实际接受的剧集演员，而不是混入的 crew。 ");
            Assert.IsNull(store.Peek(currentSeries.Id), "UserRefresh 不应保存 overwrite candidate。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenSeriesAggregateCreditsDifferFromCredits()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
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
                },
                new[]
                {
                    CreateCrewCredit(5201, "Raw Director Accepted", "Production", "Director"),
                });
            SetTmdbSeriesAggregateCredits(
                seededSeries,
                new[]
                {
                    CreateAggregateCastCredit(5101, "Raw Actor Accepted", "角色A", 0, 11),
                    CreateAggregateCastCredit(5102, "Second Aggregate Actor", "角色B", 1, 6),
                    CreateAggregateCastCredit(5103, "Third Aggregate Actor", "角色C", 2, 4),
                },
                Array.Empty<Dictionary<string, object?>>());
            SeedTmdbSeries(tmdbApi, 456, "zh-CN", seededSeries);
            SeedTmdbPerson(tmdbApi, 5101, "剧集演员大陆名 A", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 5102, "剧集演员大陆名 B", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 5103, "剧集演员大陆名 C", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 5201, "剧集导演大陆名", language: "zh-CN");

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
            Assert.AreEqual(3, result.People?.Count ?? 0, "当 aggregate cast 与 credits 不一致时，应按 aggregate cast 计数。 ");
            CollectionAssert.AreEqual(new[] { 5101, 5102, 5103 }, result.People!.Select(GetTmdbId).ToArray());
            Assert.IsNull(store.Peek(currentSeries.Id), "UserRefresh 不应保存 aggregate cast candidate。 ");
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
            Assert.IsNull(store.Peek(currentSeries.Id), "AutomatedRefresh 不应保存 overwrite candidate。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenRequestIsExplicitSearchMissingRefresh()
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
                new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentSeries.Id, replaceAllMetadata: false, metadataRefreshMode: "FullRefresh") },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsNull(store.Peek(currentSeries.Id), "SearchMissing/FullRefresh 不应保存 overwrite candidate。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldUseTmdbOnlySearchDuringOverwriteRefresh_WhenOldDoubanIdExistsWithoutTmdbId()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var configuration = plugin.Configuration;
            var originalMode = configuration.DefaultScraperMode;
            var originalEnableTmdb = configuration.EnableTmdb;
            var originalEnableTmdbMatch = configuration.EnableTmdbMatch;

            try
            {
                configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;
                configuration.EnableTmdb = true;
                configuration.EnableTmdbMatch = true;

                using var loggerFactory = LoggerFactory.Create(builder => { });
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbSeriesSearchResults(
                    tmdbApi,
                    "覆盖刷新剧集",
                    "zh-CN",
                    new SearchTv
                    {
                        Id = 9403,
                        Name = "覆盖刷新剧集",
                        OriginalName = "Overwrite Refresh Series",
                        FirstAirDate = new DateTime(2024, 6, 1),
                    });
                SeedTmdbSeries(
                    tmdbApi,
                    9403,
                    "zh-CN",
                    new TvShow
                    {
                        Id = 9403,
                        Name = "TMDb 覆盖刷新剧集",
                        OriginalName = "TMDb Overwrite Refresh Series",
                        Overview = "TMDb 覆盖刷新剧集简介",
                        FirstAirDate = new DateTime(2024, 6, 1),
                        VoteAverage = 8.1,
                        EpisodeRunTime = new List<int>(),
                        ContentRatings = new ResultContainer
                        {
                            Results = new List<ContentRating>(),
                        },
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new SeriesInfo
                {
                    Name = "覆盖刷新剧集",
                    Path = "/library/tv/覆盖刷新剧集",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [MetaSharkPlugin.ProviderId] = "Douban_old-series-sid",
                        [BaseProvider.DoubanProviderId] = "old-series-sid",
                    },
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

                var doubanApi = CreateThrowingDoubanApi(loggerFactory, "tmdb-only 覆盖刷新剧集链路不应访问 Douban。");
                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentSeries.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "tmdb-only + overwrite refresh 不应让旧 Douban sid 阻止 TMDb 搜索匹配。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("9403", result.Item!.GetProviderId(MetadataProvider.Tmdb));
                Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId), "tmdb-only 结果不应携带旧 Douban provider id。 ");
                Assert.AreEqual("TMDb 覆盖刷新剧集", result.Item.Name);
                Assert.AreEqual("TMDb 覆盖刷新剧集简介", result.Item.Overview);
            }
            finally
            {
                configuration.DefaultScraperMode = originalMode;
                configuration.EnableTmdb = originalEnableTmdb;
                configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldUseExistingTmdbIdDuringTmdbOnlyOverwriteRefresh_WhenOldDoubanIdExists()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var configuration = plugin.Configuration;
            var originalMode = configuration.DefaultScraperMode;
            var originalEnableTmdb = configuration.EnableTmdb;
            var originalEnableTmdbMatch = configuration.EnableTmdbMatch;

            try
            {
                configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;
                configuration.EnableTmdb = true;
                configuration.EnableTmdbMatch = true;

                using var loggerFactory = LoggerFactory.Create(builder => { });
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbSeries(
                    tmdbApi,
                    9406,
                    "zh-CN",
                    new TvShow
                    {
                        Id = 9406,
                        Name = "TMDb 已有 ID 覆盖剧集",
                        OriginalName = "TMDb Existing Id Overwrite Series",
                        Overview = "TMDb 已有 ID 覆盖剧集简介",
                        FirstAirDate = new DateTime(2024, 6, 4),
                        VoteAverage = 8.2,
                        EpisodeRunTime = new List<int>(),
                        ContentRatings = new ResultContainer
                        {
                            Results = new List<ContentRating>(),
                        },
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new SeriesInfo
                {
                    Name = "已有 ID 覆盖剧集",
                    Path = "/library/tv/已有 ID 覆盖剧集",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [MetaSharkPlugin.ProviderId] = "Douban_old-series-with-tmdb",
                        [BaseProvider.DoubanProviderId] = "old-series-with-tmdb",
                        [MetadataProvider.Tmdb.ToString()] = "9406",
                    },
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

                var doubanApi = CreateThrowingDoubanApi(loggerFactory, "tmdb-only 已有 TMDb id 的剧集覆盖刷新不应访问 Douban。");
                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentSeries.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "tmdb-only + overwrite refresh 已有 TMDb id 时应直达 TMDb。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("9406", result.Item!.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("Tmdb_9406", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId), "tmdb-only 已有 TMDb id 结果不应携带旧 Douban provider id。 ");
                Assert.AreEqual("TMDb 已有 ID 覆盖剧集", result.Item.Name);
                Assert.AreEqual("TMDb 已有 ID 覆盖剧集简介", result.Item.Overview);
            }
            finally
            {
                configuration.DefaultScraperMode = originalMode;
                configuration.EnableTmdb = originalEnableTmdb;
                configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldKeepDoubanPrimaryInDefaultOverwriteRefresh_WhenDoubanSubjectExists()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var configuration = plugin.Configuration;
            var originalMode = configuration.DefaultScraperMode;
            var originalEnableTmdb = configuration.EnableTmdb;
            var originalEnableTmdbMatch = configuration.EnableTmdbMatch;

            try
            {
                configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeDefault;
                configuration.EnableTmdb = true;
                configuration.EnableTmdbMatch = true;

                using var loggerFactory = LoggerFactory.Create(builder => { });
                var doubanApi = new DoubanApi(loggerFactory);
                SeedDoubanSubject(
                    doubanApi,
                    new DoubanSubject
                    {
                        Sid = "series-douban-default-primary",
                        Name = "豆瓣默认主剧集",
                        OriginalName = "Douban Default Primary Series",
                        Year = 2024,
                        Rating = 8.4f,
                        Genre = "剧情 / 动画",
                        Intro = "豆瓣默认主剧集简介",
                        Screen = "2024-06-02",
                        Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000301.webp",
                    });
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbSeriesSearchResults(
                    tmdbApi,
                    "豆瓣默认主剧集",
                    "zh-CN",
                    new SearchTv
                    {
                        Id = 9404,
                        Name = "豆瓣默认主剧集",
                        OriginalName = "Douban Default Primary Series",
                        FirstAirDate = new DateTime(2024, 6, 2),
                    });
                SeedTmdbSeries(
                    tmdbApi,
                    9404,
                    "zh-CN",
                    new TvShow
                    {
                        Id = 9404,
                        Name = "TMDb 不应主导剧集",
                        OriginalName = "TMDb Should Not Be Primary Series",
                        Overview = "TMDb 不应覆盖豆瓣剧集简介",
                        FirstAirDate = new DateTime(2024, 6, 2),
                        VoteAverage = 6.3,
                        EpisodeRunTime = new List<int>(),
                        ContentRatings = new ResultContainer
                        {
                            Results = new List<ContentRating>(),
                        },
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new SeriesInfo
                {
                    Name = "豆瓣默认主剧集",
                    Path = "/library/tv/豆瓣默认主剧集",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [BaseProvider.DoubanProviderId] = "series-douban-default-primary",
                        [MetaSharkPlugin.ProviderId] = "Douban_series-douban-default-primary",
                        [MetadataProvider.Tmdb.ToString()] = "9404",
                    },
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

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentSeries.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item);
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("豆瓣默认主剧集", result.Item!.Name);
                Assert.AreEqual("豆瓣默认主剧集简介", result.Item.Overview);
                Assert.AreEqual("series-douban-default-primary", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
                Assert.AreEqual("Douban_series-douban-default-primary", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            }
            finally
            {
                configuration.DefaultScraperMode = originalMode;
                configuration.EnableTmdb = originalEnableTmdb;
                configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldGuessDoubanInDefaultOverwriteRefresh_WhenExistingTmdbSourceAndIdHaveNoDoubanId()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var configuration = plugin.Configuration;
            var originalMode = configuration.DefaultScraperMode;
            var originalEnableTmdb = configuration.EnableTmdb;
            var originalEnableTmdbMatch = configuration.EnableTmdbMatch;

            try
            {
                configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeDefault;
                configuration.EnableTmdb = true;
                configuration.EnableTmdbMatch = true;

                using var loggerFactory = LoggerFactory.Create(builder => { });
                var doubanApi = new DoubanApi(loggerFactory);
                var doubanSubject = new DoubanSubject
                {
                    Sid = "series-douban-guessed-overwrite",
                    Name = "豆瓣覆盖剧集甲",
                    OriginalName = "Douban Overwrite Series A",
                    Year = 0,
                    Category = "电视剧",
                    Rating = 8.7f,
                    Genre = "剧情 / 动画",
                    Intro = "豆瓣覆盖剧集甲简介",
                    Screen = "2024-06-05",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000701.webp",
                };
                SeedDoubanSearchResults(doubanApi, "覆盖刷新剧集甲", new[] { doubanSubject });
                SeedDoubanSubject(doubanApi, doubanSubject);

                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbSeries(
                    tmdbApi,
                    9408,
                    "zh-CN",
                    new TvShow
                    {
                        Id = 9408,
                        Name = "TMDb 不应主导剧集甲",
                        OriginalName = "TMDb Should Not Lead Series A",
                        Overview = "如果 default 覆盖刷新沿用历史 TMDb 来源就会得到这段剧集简介",
                        FirstAirDate = new DateTime(2024, 6, 5),
                        VoteAverage = 6.4,
                        EpisodeRunTime = new List<int>(),
                        ContentRatings = new ResultContainer
                        {
                            Results = new List<ContentRating>(),
                        },
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new SeriesInfo
                {
                    Name = "覆盖刷新剧集甲",
                    Path = "/library/tv/覆盖刷新剧集甲",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [MetaSharkPlugin.ProviderId] = "Tmdb_9408",
                        [MetadataProvider.Tmdb.ToString()] = "9408",
                    },
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

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentSeries.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "default + overwrite refresh 即使已有 TMDb 来源，也应先按标题猜测 Douban。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("series-douban-guessed-overwrite", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
                Assert.AreEqual("Douban_series-douban-guessed-overwrite", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.AreEqual("9408", result.Item.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("豆瓣覆盖剧集甲", result.Item.Name);
                Assert.AreEqual("豆瓣覆盖剧集甲简介", result.Item.Overview);
                Assert.AreNotEqual("TMDb 不应主导剧集甲", result.Item.Name);
            }
            finally
            {
                configuration.DefaultScraperMode = originalMode;
                configuration.EnableTmdb = originalEnableTmdb;
                configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldUseDoubanProviderIdInDefaultOverwriteRefresh_WhenExistingTmdbSourceAndIdAlsoHaveDoubanId()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var configuration = plugin.Configuration;
            var originalMode = configuration.DefaultScraperMode;
            var originalEnableTmdb = configuration.EnableTmdb;
            var originalEnableTmdbMatch = configuration.EnableTmdbMatch;

            try
            {
                configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeDefault;
                configuration.EnableTmdb = true;
                configuration.EnableTmdbMatch = true;

                using var loggerFactory = LoggerFactory.Create(builder => { });
                var doubanApi = new DoubanApi(loggerFactory);
                SeedDoubanSubject(
                    doubanApi,
                    new DoubanSubject
                    {
                        Sid = "series-douban-existing-overwrite",
                        Name = "豆瓣覆盖剧集乙",
                        OriginalName = "Douban Overwrite Series B",
                        Year = 0,
                        Category = "电视剧",
                        Rating = 8.8f,
                        Genre = "剧情 / 悬疑",
                        Intro = "豆瓣覆盖剧集乙简介",
                        Screen = "2024-06-06",
                        Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000702.webp",
                    });

                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbSeries(
                    tmdbApi,
                    9409,
                    "zh-CN",
                    new TvShow
                    {
                        Id = 9409,
                        Name = "TMDb 不应主导剧集乙",
                        OriginalName = "TMDb Should Not Lead Series B",
                        Overview = "如果已有 Douban id 仍被 Tmdb_* 抢先就会得到这段剧集简介",
                        FirstAirDate = new DateTime(2024, 6, 6),
                        VoteAverage = 6.5,
                        EpisodeRunTime = new List<int>(),
                        ContentRatings = new ResultContainer
                        {
                            Results = new List<ContentRating>(),
                        },
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new SeriesInfo
                {
                    Name = "覆盖刷新剧集乙",
                    Path = "/library/tv/覆盖刷新剧集乙",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [BaseProvider.DoubanProviderId] = "series-douban-existing-overwrite",
                        [MetaSharkPlugin.ProviderId] = "Tmdb_9409",
                        [MetadataProvider.Tmdb.ToString()] = "9409",
                    },
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

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentSeries.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "default + overwrite refresh 同时有 Douban/TMDb id 时不得让 Tmdb_* 成为主来源。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("series-douban-existing-overwrite", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
                Assert.AreEqual("Douban_series-douban-existing-overwrite", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.AreEqual("9409", result.Item.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("豆瓣覆盖剧集乙", result.Item.Name);
                Assert.AreEqual("豆瓣覆盖剧集乙简介", result.Item.Overview);
                Assert.AreNotEqual("TMDb 不应主导剧集乙", result.Item.Name);
            }
            finally
            {
                configuration.DefaultScraperMode = originalMode;
                configuration.EnableTmdb = originalEnableTmdb;
                configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldUseTmdbPrimaryFallbackInDefaultOverwriteRefresh_WhenDoubanSubjectIsMissingAndTmdbIdExists()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var configuration = plugin.Configuration;
            var originalMode = configuration.DefaultScraperMode;
            var originalEnableTmdb = configuration.EnableTmdb;
            var originalEnableTmdbMatch = configuration.EnableTmdbMatch;

            try
            {
                configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeDefault;
                configuration.EnableTmdb = true;
                configuration.EnableTmdbMatch = true;

                using var loggerFactory = LoggerFactory.Create(builder => { });
                var doubanApi = new DoubanApi(loggerFactory);
                SeedMissingDoubanSubject(doubanApi, "series-douban-missing-default");
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbSeries(
                    tmdbApi,
                    9405,
                    "zh-CN",
                    new TvShow
                    {
                        Id = 9405,
                        Name = "TMDb 默认兜底剧集",
                        OriginalName = "TMDb Default Fallback Series",
                        Overview = "TMDb 默认兜底剧集简介",
                        FirstAirDate = new DateTime(2024, 6, 3),
                        VoteAverage = 7.6,
                        EpisodeRunTime = new List<int>(),
                        ContentRatings = new ResultContainer
                        {
                            Results = new List<ContentRating>(),
                        },
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new SeriesInfo
                {
                    Name = "默认兜底剧集",
                    Path = "/library/tv/默认兜底剧集",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [BaseProvider.DoubanProviderId] = "series-douban-missing-default",
                        [MetaSharkPlugin.ProviderId] = "Douban_series-douban-missing-default",
                        [MetadataProvider.Tmdb.ToString()] = "9405",
                    },
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

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentSeries.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item);
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("9405", result.Item!.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("Tmdb_9405", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId), "default 只有在 Douban subject 缺失时才允许 TMDb 成为主兜底。 ");
                Assert.AreEqual("TMDb 默认兜底剧集", result.Item.Name);
                Assert.AreEqual("TMDb 默认兜底剧集简介", result.Item.Overview);
            }
            finally
            {
                configuration.DefaultScraperMode = originalMode;
                configuration.EnableTmdb = originalEnableTmdb;
                configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldKeepDoubanFirstInDefault_WhenLegacyMetaSharkProviderIdAndWrongDoubanCandidateExist()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var configuration = plugin.Configuration;
            var originalMode = configuration.DefaultScraperMode;
            var originalEnableTmdb = configuration.EnableTmdb;
            var originalEnableTmdbMatch = configuration.EnableTmdbMatch;

            try
            {
                configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeDefault;
                configuration.EnableTmdb = true;
                configuration.EnableTmdbMatch = true;

                using var loggerFactory = LoggerFactory.Create(builder => { });
                var doubanApi = new DoubanApi(loggerFactory);
                var wrongDoubanSubject = new DoubanSubject
                {
                    Sid = "wrong-douban-rezero-break-time",
                    Name = "Re：从零开始的休息时间",
                    OriginalName = "Re:Zero kara Hajimeru Break Time",
                    Year = 0,
                    Category = "电视剧",
                    Rating = 1.2f,
                    Genre = "动画 / 短片",
                    Intro = "如果误走 default Douban 链路就会得到休息时间简介",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000501.webp",
                };
                SeedDoubanSearchResults(doubanApi, "Re：从零开始的异世界生活", new[] { wrongDoubanSubject });
                SeedDoubanSubject(doubanApi, wrongDoubanSubject);
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbSeriesSearchResults(
                    tmdbApi,
                    "Re：从零开始的异世界生活",
                    "zh-CN",
                    new SearchTv
                    {
                        Id = 65942,
                        Name = "Re：从零开始的异世界生活",
                        OriginalName = "Re:ZERO -Starting Life in Another World-",
                        FirstAirDate = new DateTime(2016, 4, 4),
                    });
                SeedTmdbSeries(
                    tmdbApi,
                    65942,
                    "zh-CN",
                    new TvShow
                    {
                        Id = 65942,
                        Name = "Re：从零开始的异世界生活",
                        OriginalName = "Re:ZERO -Starting Life in Another World-",
                        Overview = "TMDb Re:Zero 正确剧集简介",
                        FirstAirDate = new DateTime(2016, 4, 4),
                        VoteAverage = 8.5,
                        EpisodeRunTime = new List<int>(),
                        ContentRatings = new ResultContainer
                        {
                            Results = new List<ContentRating>(),
                        },
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new SeriesInfo
                {
                    Name = "Re：从零开始的异世界生活",
                    Path = "/library/tv/Re：从零开始的异世界生活",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [MetaSharkPlugin.ProviderId] = "Tmdb_1111",
                    },
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

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = null },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "default 配置必须保持 Douban-first，不应因 MetaSharkID=Tmdb_* 强制改走 TMDb。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("wrong-douban-rezero-break-time", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
                Assert.AreEqual("Douban_wrong-douban-rezero-break-time", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.IsNull(result.Item.GetProviderId(MetadataProvider.Tmdb), "MetaSharkID=Tmdb_1111 不能被解析成官方 TMDb id。 ");
                Assert.AreEqual("Re：从零开始的休息时间", result.Item.Name);
                Assert.AreNotEqual("Re：从零开始的异世界生活", result.Item.Name);
                Assert.AreEqual("如果误走 default Douban 链路就会得到休息时间简介", result.Item.Overview);
            }
            finally
            {
                configuration.DefaultScraperMode = originalMode;
                configuration.EnableTmdb = originalEnableTmdb;
                configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldSearchAndWriteStandardTmdbId_WhenOnlyHistoricalMetaSharkTmdbIdExistsAndDoubanSubjectIsMissing()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var configuration = plugin.Configuration;
            var originalMode = configuration.DefaultScraperMode;
            var originalEnableTmdb = configuration.EnableTmdb;
            var originalEnableTmdbMatch = configuration.EnableTmdbMatch;

            try
            {
                configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeDefault;
                configuration.EnableTmdb = true;
                configuration.EnableTmdbMatch = true;

                using var loggerFactory = LoggerFactory.Create(builder => { });
                var doubanApi = new DoubanApi(loggerFactory);
                SeedMissingDoubanSubject(doubanApi, "series-douban-missing-legacy-tmdb");
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbSeriesSearchResults(
                    tmdbApi,
                    "Historical Legacy Series",
                    "zh-CN",
                    new SearchTv
                    {
                        Id = 9405,
                        Name = "Historical Legacy Series",
                        OriginalName = "TMDb Historical Legacy Series",
                        FirstAirDate = new DateTime(2024, 6, 3),
                    });
                SeedTmdbSeries(
                    tmdbApi,
                    2222,
                    "zh-CN",
                    new TvShow
                    {
                        Id = 2222,
                        Name = "错误 historical legacy 剧集",
                        OriginalName = "Wrong Historical Legacy Series",
                        Overview = "如果复用 MetaSharkTmdbID=2222 就会得到这段错误简介",
                        FirstAirDate = new DateTime(2024, 1, 1),
                        VoteAverage = 1.2,
                        EpisodeRunTime = new List<int>(),
                        ContentRatings = new ResultContainer
                        {
                            Results = new List<ContentRating>(),
                        },
                    });
                SeedTmdbSeries(
                    tmdbApi,
                    9405,
                    "zh-CN",
                    new TvShow
                    {
                        Id = 9405,
                        Name = "TMDb historical legacy 剧集",
                        OriginalName = "TMDb Historical Legacy Series",
                        Overview = "TMDb historical legacy 默认兜底剧集简介",
                        FirstAirDate = new DateTime(2024, 6, 3),
                        VoteAverage = 7.6,
                        EpisodeRunTime = new List<int>(),
                        ContentRatings = new ResultContainer
                        {
                            Results = new List<ContentRating>(),
                        },
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new SeriesInfo
                {
                    Name = "Historical Legacy Series",
                    Path = "/library/tv/Historical Legacy Series",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [BaseProvider.DoubanProviderId] = "series-douban-missing-legacy-tmdb",
                        [MetaSharkPlugin.ProviderId] = "Douban_series-douban-missing-legacy-tmdb",
                        ["MetaSharkTmdbID"] = "2222",
                    },
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

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = null },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "default 下 Douban subject 缺失后应按标题搜索 TMDb，而不是复用历史 MetaSharkTmdbID。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("9405", result.Item!.GetProviderId(MetadataProvider.Tmdb), "旧 MetaSharkTmdbID=2222 不能作为有效 TMDb id；搜索命中的 9405 必须写成官方 TMDb provider id。 ");
                Assert.AreEqual("Tmdb_9405", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId), "Douban subject 缺失后返回的 TMDb 兜底结果不应沿用旧 Douban provider id。 ");
                Assert.AreEqual("TMDb historical legacy 剧集", result.Item.Name);
                Assert.AreEqual("TMDb historical legacy 默认兜底剧集简介", result.Item.Overview);
            }
            finally
            {
                configuration.DefaultScraperMode = originalMode;
                configuration.EnableTmdb = originalEnableTmdb;
                configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldSaveCandidate_WhenRequestIsManualMatch()
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
            var candidate = store.Peek(currentSeries.Id);
            Assert.IsNotNull(candidate, "ManualMatch /Items/RemoteSearch/Apply 命中 TMDb provider 时，应创建单项 overwrite candidate。 ");
            Assert.AreEqual(currentSeries.Id, candidate!.ItemId);
            Assert.AreEqual(info.Path, candidate.ItemPath);
            Assert.AreEqual(result.People?.Count ?? 0, candidate.ExpectedPeopleCount);
            AssertAuthoritativeSnapshot(candidate, nameof(Series), "456", result.People);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldRearmQueuedCandidate_WhenRequestIsManualMatch()
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
                CreateManualMatchContextAccessor(),
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            var candidate = store.Peek(currentSeries.Id);
            Assert.IsNotNull(candidate, "ManualMatch 命中已有 queued candidate 时，应重新武装并更新该 candidate。 ");
            Assert.AreEqual(currentSeries.Id, candidate!.ItemId);
            Assert.AreEqual(info.Path, candidate.ItemPath);
            Assert.AreEqual(result.People?.Count ?? 0, candidate.ExpectedPeopleCount);
            Assert.IsFalse(candidate.OverwriteQueued, "ManualMatch 应把 queued candidate 重新武装为未排队状态。 ");
            AssertAuthoritativeSnapshot(candidate, nameof(Series), "456", result.People);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenTmdbPeopleIsEmptyButCurrentSeriesStillHasPeople()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 456, "zh-CN", "示例剧集");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateSeriesInfo();
            info.IsAutomated = false;
            var currentSeries = new AuthoritativeTrackingSeries
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };
            currentSeries.SetProviderId(MetadataProvider.Tmdb, "456");
            currentSeries.SetSimulatedPeople(new[]
            {
                CreateCurrentPerson("旧演员A", "角色A", PersonKind.Actor),
                CreateCurrentPerson("旧导演A", "Director", PersonKind.Director),
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
            Assert.AreEqual(0, result.People?.Count ?? 0, "测试前提：TMDb authoritative 应为空。 ");
            Assert.IsNull(store.Peek(currentSeries.Id), "UserRefresh 不应保存空 authoritative candidate。 ");
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

        [TestMethod]
        public async Task GetMetadata_ShouldKeepQueuedCandidate_WhenExplicitRefreshRequestWithoutReplaceAllMetadata()
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
                new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentSeries.Id, replaceAllMetadata: false) },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            var candidate = store.Peek(currentSeries.Id);
            Assert.IsNotNull(candidate, "UserRefresh 不应改写已排队的 queued candidate。 ");
            Assert.AreEqual(17, candidate!.ExpectedPeopleCount, "UserRefresh 不应覆盖已有 candidate 的 expected people 数。 ");
            Assert.IsTrue(candidate.OverwriteQueued, "UserRefresh 下 queued candidate 应保持已排队状态。 ");
            Assert.IsNull(candidate.AuthoritativePeopleSnapshot, "UserRefresh 不应重写 queued candidate 的 authoritative 快照。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldKeepQueuedCandidate_WhenExplicitRefreshRequestUsesReplaceAllMetadata()
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
                new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentSeries.Id, replaceAllMetadata: true) },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            var candidate = store.Peek(currentSeries.Id);
            Assert.IsNotNull(candidate, "ReplaceAllMetadata=true 的 overwrite refresh 不应打破现有 queued candidate 幂等性。 ");
            Assert.AreEqual(17, candidate!.ExpectedPeopleCount, "overwrite refresh 不应被新的未排队 candidate 覆盖。 ");
            Assert.IsTrue(candidate.OverwriteQueued, "ReplaceAllMetadata=true 时，queued candidate 仍应保持已排队状态。 ");
            Assert.AreEqual(currentSeries.Path, candidate.ItemPath);
        }

        private static SeriesProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IMovieSeriesPeopleOverwriteRefreshCandidateStore store,
            ILoggerFactory loggerFactory,
            DoubanApi? doubanApi = null)
        {
            return new SeriesProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager,
                httpContextAccessor,
                doubanApi ?? new DoubanApi(loggerFactory),
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

        private static DefaultHttpContext CreateRefreshRequestContext(Guid itemId, bool replaceAllMetadata, string? metadataRefreshMode = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/Items/{itemId:N}/Refresh";
            var queryString = $"?replaceAllMetadata={replaceAllMetadata.ToString().ToLowerInvariant()}";

            if (!string.IsNullOrWhiteSpace(metadataRefreshMode))
            {
                queryString += $"&metadataRefreshMode={metadataRefreshMode}";
            }

            context.Request.QueryString = new QueryString(queryString);
            return context;
        }

        private static void AssertAuthoritativeSnapshot(MovieSeriesPeopleOverwriteRefreshCandidate candidate, string itemType, string tmdbId, IEnumerable<PersonInfo>? people)
        {
            Assert.IsNotNull(candidate.AuthoritativePeopleSnapshot, "candidate 应携带 authoritative people 快照。 ");
            Assert.AreEqual(itemType, candidate.AuthoritativePeopleSnapshot!.ItemType);
            Assert.AreEqual(tmdbId, candidate.AuthoritativePeopleSnapshot.TmdbId);
            Assert.IsTrue(
                candidate.AuthoritativePeopleSnapshot.SetEquals(TmdbAuthoritativePeopleSnapshot.Create(itemType, tmdbId, people ?? Array.Empty<PersonInfo>())),
                "candidate authoritative 快照应与 provider 最终产出的 TMDb people 指纹一致。 ");
        }

        private static PersonInfo CreateCurrentPerson(string name, string role, PersonKind type)
        {
            return new PersonInfo
            {
                Name = name,
                Role = role,
                Type = type,
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

        private static int GetTmdbId(PersonInfo person)
        {
            Assert.IsNotNull(person.ProviderIds, "人物缺少 provider ids。 ");
            Assert.IsTrue(person.ProviderIds!.TryGetValue(MetadataProvider.Tmdb.ToString(), out var providerId), "人物缺少 TMDb provider id。 ");
            Assert.IsTrue(int.TryParse(providerId, out var tmdbId), $"人物 TMDb provider id 无法解析为整数: {providerId}");
            return tmdbId;
        }

        private static Dictionary<string, object?> CreateAggregateCastCredit(int id, string name, string character, int order, int totalEpisodeCount)
        {
            return new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["Name"] = name,
                ["Order"] = order,
                ["TotalEpisodeCount"] = totalEpisodeCount,
                ["Roles"] = CreateAggregateCastRoles((character, totalEpisodeCount)),
            };
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

        private static void SeedMissingDoubanSubject(DoubanApi doubanApi, string sid)
        {
            var cache = GetDoubanMemoryCache(doubanApi);
            cache.Set<DoubanSubject?>($"movie_{sid}", null, TimeSpan.FromMinutes(5));
            cache.Set($"celebrities_{sid}", new List<DoubanCelebrity>(), TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanSearchResults(DoubanApi doubanApi, string keyword, IReadOnlyCollection<DoubanSubject> subjects)
        {
            GetDoubanMemoryCache(doubanApi).Set(
                $"search_{keyword}",
                subjects.ToList(),
                TimeSpan.FromMinutes(5));
        }

        private static MemoryCache GetTmdbMemoryCache(TmdbApi tmdbApi)
        {
            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "TmdbApi.memoryCache 未找到");

            var memoryCache = memoryCacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(memoryCache, "TmdbApi.memoryCache 不是有效的 MemoryCache");
            return memoryCache!;
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
    }
}
