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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections;
using TmdbMovie = TMDbLib.Objects.Movies.Movie;
using TmdbGenre = TMDbLib.Objects.General.Genre;
using ProductionCountry = TMDbLib.Objects.General.ProductionCountry;
using TmdbPerson = TMDbLib.Objects.People.Person;
using TmdbTranslationData = TMDbLib.Objects.General.TranslationData;
using TMDbLib.Client;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class MovieProviderSearchMissingMetadataRequestTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-movie-provider-request-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenInfoRepresentsUserRefresh()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateMovieInfo();
            info.IsAutomated = false;
            var currentMovie = new Movie
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(currentMovie);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = null },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsNull(store.Peek(currentMovie.Id), "UserRefresh 不应保存 overwrite candidate。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenDoubanBranchBackfillsPeopleDuringUserRefresh()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            SeedDoubanSubject(
                doubanApi,
                new DoubanSubject
                {
                    Sid = "movie-douban-123",
                    Name = "豆瓣示例电影",
                    OriginalName = "Douban Sample Movie",
                    Year = 2024,
                    Rating = 8.6f,
                    Genre = "剧情 / 动画",
                    Intro = "豆瓣电影简介",
                    Screen = "2024-03-03",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000100.webp",
                });

            var tmdbApi = new TmdbApi(loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            var seededMovie = new TmdbMovie
            {
                Id = 123,
                Title = "TMDb 占位电影",
                OriginalTitle = "TMDb Placeholder Movie",
                ImdbId = "tt0000001",
                Overview = "TMDb seeded movie overview",
                Tagline = "TMDb seeded movie tagline",
                ReleaseDate = new DateTime(2024, 3, 3),
                VoteAverage = 8.6,
                ProductionCountries = new List<ProductionCountry>(),
                Genres = new List<TmdbGenre>(),
            };
            SetTmdbMovieCredits(
                seededMovie,
                new[]
                {
                    CreateCastCredit(3001, "Raw Accepted Actor", "角色甲", 0, "/accepted-actor.jpg"),
                    CreateCastCredit(3002, "Raw Rejected Actor", "角色乙", 1, "/rejected-actor.jpg"),
                },
                new[]
                {
                    new Dictionary<string, object?> { ["Id"] = 4001, ["Name"] = string.Empty, ["Department"] = "Production", ["Job"] = "Director", ["ProfilePath"] = "/accepted-director.jpg" },
                });
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", seededMovie);
            SeedTmdbPerson(tmdbApi, 3001, "这个演员甲", language: "zh-CN");
            SeedBlankExactZhCnPerson(tmdbApi, 3002);
            SeedTmdbPerson(tmdbApi, 4001, string.Empty, language: "zh-CN");
            SeedTmdbPersonTranslations(tmdbApi, 4001, CreatePersonTranslation("zh", "CN", "这个导演甲"));

            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = new MovieInfo
            {
                Name = "豆瓣示例电影",
                Path = "/library/movies/sample-movie/sample-movie.mkv",
                MetadataLanguage = "zh-CN",
                IsAutomated = false,
                ProviderIds = new Dictionary<string, string>
                {
                    [MetaSharkPlugin.ProviderId] = "Douban_movie-douban-123",
                    [BaseProvider.DoubanProviderId] = "movie-douban-123",
                    [MetadataProvider.Tmdb.ToString()] = "123",
                },
            };
            var currentMovie = new Movie
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(currentMovie);

            var provider = new MovieProvider(
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
            Assert.IsNotNull(result.People);
            Assert.AreEqual(2, result.People.Count, "UserRefresh 下 Douban 主分支仍应正确统计实际 accepted 的 TMDb people。 ");
            Assert.IsNull(store.Peek(currentMovie.Id), "UserRefresh 不应保存 overwrite candidate。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenInfoRepresentsAutomatedRefresh()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateMovieInfo();
            info.IsAutomated = true;
            var currentMovie = new Movie
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(currentMovie);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = null },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsNull(store.Peek(currentMovie.Id), "AutomatedRefresh 不应保存 overwrite candidate。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenRequestIsExplicitSearchMissingRefresh()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateMovieInfo();
            info.IsAutomated = false;
            var currentMovie = new Movie
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(currentMovie);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentMovie.Id, replaceAllMetadata: false, metadataRefreshMode: "FullRefresh") },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsNull(store.Peek(currentMovie.Id), "SearchMissing/FullRefresh 不应保存 overwrite candidate。 ");
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
                SeedTmdbMovieSearchResults(
                    tmdbApi,
                    "覆盖刷新电影",
                    0,
                    "zh-CN",
                    new SearchMovie
                    {
                        Id = 9303,
                        Title = "覆盖刷新电影",
                        OriginalTitle = "Overwrite Refresh Movie",
                        ReleaseDate = new DateTime(2024, 5, 1),
                    });
                SeedTmdbMovie(
                    tmdbApi,
                    9303,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 9303,
                        Title = "TMDb 覆盖刷新电影",
                        OriginalTitle = "TMDb Overwrite Refresh Movie",
                        ImdbId = "tt0009303",
                        Overview = "TMDb 覆盖刷新简介",
                        Tagline = "TMDb overwrite refresh tagline",
                        ReleaseDate = new DateTime(2024, 5, 1),
                        VoteAverage = 7.9,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new MovieInfo
                {
                    Name = "覆盖刷新电影",
                    Path = "/library/movies/覆盖刷新电影/覆盖刷新电影.mkv",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [MetaSharkPlugin.ProviderId] = "Douban_old-movie-sid",
                        [BaseProvider.DoubanProviderId] = "old-movie-sid",
                    },
                };
                var currentMovie = new Movie
                {
                    Id = Guid.NewGuid(),
                    Path = info.Path,
                };

                var libraryManagerStub = new Mock<ILibraryManager>();
                libraryManagerStub
                    .Setup(x => x.FindByPath(info.Path, false))
                    .Returns(currentMovie);

                var doubanApi = CreateThrowingDoubanApi(loggerFactory, "tmdb-only 覆盖刷新电影链路不应访问 Douban。");
                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentMovie.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "tmdb-only + overwrite refresh 不应让旧 Douban sid 阻止 TMDb 搜索匹配。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("9303", result.Item!.GetProviderId(MetadataProvider.Tmdb));
                Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId), "tmdb-only 结果不应携带旧 Douban provider id。 ");
                Assert.AreEqual("TMDb 覆盖刷新电影", result.Item.Name);
                Assert.AreEqual("TMDb 覆盖刷新简介", result.Item.Overview);
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
                SeedTmdbMovie(
                    tmdbApi,
                    9306,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 9306,
                        Title = "TMDb 已有 ID 覆盖电影",
                        OriginalTitle = "TMDb Existing Id Overwrite Movie",
                        ImdbId = "tt0009306",
                        Overview = "TMDb 已有 ID 覆盖电影简介",
                        Tagline = "TMDb existing id overwrite tagline",
                        ReleaseDate = new DateTime(2024, 5, 4),
                        VoteAverage = 8.0,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new MovieInfo
                {
                    Name = "已有 ID 覆盖电影",
                    Path = "/library/movies/已有 ID 覆盖电影/已有 ID 覆盖电影.mkv",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [MetaSharkPlugin.ProviderId] = "Douban_old-movie-with-tmdb",
                        [BaseProvider.DoubanProviderId] = "old-movie-with-tmdb",
                        [MetadataProvider.Tmdb.ToString()] = "9306",
                    },
                };
                var currentMovie = new Movie
                {
                    Id = Guid.NewGuid(),
                    Path = info.Path,
                };

                var libraryManagerStub = new Mock<ILibraryManager>();
                libraryManagerStub
                    .Setup(x => x.FindByPath(info.Path, false))
                    .Returns(currentMovie);

                var doubanApi = CreateThrowingDoubanApi(loggerFactory, "tmdb-only 已有 TMDb id 的电影覆盖刷新不应访问 Douban。");
                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentMovie.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "tmdb-only + overwrite refresh 已有 TMDb id 时应直达 TMDb。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("9306", result.Item!.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("Tmdb_9306", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId), "tmdb-only 已有 TMDb id 结果不应携带旧 Douban provider id。 ");
                Assert.AreEqual("TMDb 已有 ID 覆盖电影", result.Item.Name);
                Assert.AreEqual("TMDb 已有 ID 覆盖电影简介", result.Item.Overview);
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
                        Sid = "movie-douban-default-primary",
                        Name = "豆瓣默认主电影",
                        OriginalName = "Douban Default Primary Movie",
                        Year = 2024,
                        Rating = 8.2f,
                        Genre = "剧情 / 动画",
                        Intro = "豆瓣默认主电影简介",
                        Screen = "2024-05-02",
                        Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000201.webp",
                    });
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbMovie(
                    tmdbApi,
                    9304,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 9304,
                        Title = "TMDb 不应主导电影",
                        OriginalTitle = "TMDb Should Not Be Primary Movie",
                        ImdbId = "tt0009304",
                        Overview = "TMDb 不应覆盖豆瓣简介",
                        Tagline = "TMDb default-mode tagline",
                        ReleaseDate = new DateTime(2024, 5, 2),
                        VoteAverage = 6.1,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new MovieInfo
                {
                    Name = "豆瓣默认主电影",
                    Path = "/library/movies/豆瓣默认主电影/豆瓣默认主电影.mkv",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [BaseProvider.DoubanProviderId] = "movie-douban-default-primary",
                        [MetaSharkPlugin.ProviderId] = "Douban_movie-douban-default-primary",
                        [MetadataProvider.Tmdb.ToString()] = "9304",
                    },
                };
                var currentMovie = new Movie
                {
                    Id = Guid.NewGuid(),
                    Path = info.Path,
                };

                var libraryManagerStub = new Mock<ILibraryManager>();
                libraryManagerStub
                    .Setup(x => x.FindByPath(info.Path, false))
                    .Returns(currentMovie);

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentMovie.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item);
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("豆瓣默认主电影", result.Item!.Name);
                Assert.AreEqual("豆瓣默认主电影简介", result.Item.Overview);
                Assert.AreEqual("movie-douban-default-primary", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
                Assert.AreEqual("Douban_movie-douban-default-primary", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
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
                    Sid = "movie-douban-guessed-overwrite",
                    Name = "豆瓣覆盖电影甲",
                    OriginalName = "Douban Overwrite Movie A",
                    Year = 0,
                    Category = "电影",
                    Rating = 8.5f,
                    Genre = "剧情 / 动画",
                    Intro = "豆瓣覆盖电影甲简介",
                    Screen = "2024-05-05",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000601.webp",
                };
                SeedDoubanSearchResults(doubanApi, "覆盖刷新电影甲", new[] { doubanSubject });
                SeedDoubanSubject(doubanApi, doubanSubject);

                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbMovie(
                    tmdbApi,
                    9308,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 9308,
                        Title = "TMDb 不应主导电影甲",
                        OriginalTitle = "TMDb Should Not Lead Movie A",
                        ImdbId = "tt0009308",
                        Overview = "如果 default 覆盖刷新沿用历史 TMDb 来源就会得到这段简介",
                        Tagline = "TMDb wrong primary tagline",
                        ReleaseDate = new DateTime(2024, 5, 5),
                        VoteAverage = 6.1,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new MovieInfo
                {
                    Name = "覆盖刷新电影甲",
                    Path = "/library/movies/覆盖刷新电影甲/覆盖刷新电影甲.mkv",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [MetaSharkPlugin.ProviderId] = "Tmdb_9308",
                        [MetadataProvider.Tmdb.ToString()] = "9308",
                    },
                };
                var currentMovie = new Movie
                {
                    Id = Guid.NewGuid(),
                    Path = info.Path,
                };

                var libraryManagerStub = new Mock<ILibraryManager>();
                libraryManagerStub
                    .Setup(x => x.FindByPath(info.Path, false))
                    .Returns(currentMovie);

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentMovie.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "default + overwrite refresh 即使已有 TMDb 来源，也应先按标题猜测 Douban。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("movie-douban-guessed-overwrite", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
                Assert.AreEqual("Douban_movie-douban-guessed-overwrite", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.AreEqual("9308", result.Item.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("豆瓣覆盖电影甲", result.Item.Name);
                Assert.AreEqual("豆瓣覆盖电影甲简介", result.Item.Overview);
                Assert.AreNotEqual("TMDb 不应主导电影甲", result.Item.Name);
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
                        Sid = "movie-douban-existing-overwrite",
                        Name = "豆瓣覆盖电影乙",
                        OriginalName = "Douban Overwrite Movie B",
                        Year = 2024,
                        Category = "电影",
                        Rating = 8.6f,
                        Genre = "剧情 / 悬疑",
                        Intro = "豆瓣覆盖电影乙简介",
                        Screen = "2024-05-06",
                        Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000602.webp",
                    });

                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbMovie(
                    tmdbApi,
                    9309,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 9309,
                        Title = "TMDb 不应主导电影乙",
                        OriginalTitle = "TMDb Should Not Lead Movie B",
                        ImdbId = "tt0009309",
                        Overview = "如果已有 Douban id 仍被 Tmdb_* 抢先就会得到这段简介",
                        Tagline = "TMDb wrong primary tagline B",
                        ReleaseDate = new DateTime(2024, 5, 6),
                        VoteAverage = 6.2,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new MovieInfo
                {
                    Name = "覆盖刷新电影乙",
                    Path = "/library/movies/覆盖刷新电影乙/覆盖刷新电影乙.mkv",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [BaseProvider.DoubanProviderId] = "movie-douban-existing-overwrite",
                        [MetaSharkPlugin.ProviderId] = "Tmdb_9309",
                        [MetadataProvider.Tmdb.ToString()] = "9309",
                    },
                };
                var currentMovie = new Movie
                {
                    Id = Guid.NewGuid(),
                    Path = info.Path,
                };

                var libraryManagerStub = new Mock<ILibraryManager>();
                libraryManagerStub
                    .Setup(x => x.FindByPath(info.Path, false))
                    .Returns(currentMovie);

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentMovie.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "default + overwrite refresh 同时有 Douban/TMDb id 时不得让 Tmdb_* 成为主来源。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("movie-douban-existing-overwrite", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
                Assert.AreEqual("Douban_movie-douban-existing-overwrite", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.AreEqual("9309", result.Item.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("豆瓣覆盖电影乙", result.Item.Name);
                Assert.AreEqual("豆瓣覆盖电影乙简介", result.Item.Overview);
                Assert.AreNotEqual("TMDb 不应主导电影乙", result.Item.Name);
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
                SeedMissingDoubanSubject(doubanApi, "movie-douban-missing-default");
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbMovie(
                    tmdbApi,
                    9305,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 9305,
                        Title = "TMDb 默认兜底电影",
                        OriginalTitle = "TMDb Default Fallback Movie",
                        ImdbId = "tt0009305",
                        Overview = "TMDb 默认兜底简介",
                        Tagline = "TMDb default fallback tagline",
                        ReleaseDate = new DateTime(2024, 5, 3),
                        VoteAverage = 7.4,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new MovieInfo
                {
                    Name = "默认兜底电影",
                    Path = "/library/movies/默认兜底电影/默认兜底电影.mkv",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [BaseProvider.DoubanProviderId] = "movie-douban-missing-default",
                        [MetaSharkPlugin.ProviderId] = "Douban_movie-douban-missing-default",
                        [MetadataProvider.Tmdb.ToString()] = "9305",
                    },
                };
                var currentMovie = new Movie
                {
                    Id = Guid.NewGuid(),
                    Path = info.Path,
                };

                var libraryManagerStub = new Mock<ILibraryManager>();
                libraryManagerStub
                    .Setup(x => x.FindByPath(info.Path, false))
                    .Returns(currentMovie);

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentMovie.Id, replaceAllMetadata: true) },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item);
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("9305", result.Item!.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("Tmdb_9305", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId), "default 只有在 Douban subject 缺失时才允许 TMDb 成为主兜底。 ");
                Assert.AreEqual("TMDb 默认兜底电影", result.Item.Name);
                Assert.AreEqual("TMDb 默认兜底简介", result.Item.Overview);
            }
            finally
            {
                configuration.DefaultScraperMode = originalMode;
                configuration.EnableTmdb = originalEnableTmdb;
                configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldSearchAndWriteStandardTmdbId_WhenOnlyLegacyMetaSharkProviderIdExists()
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
                SeedDoubanSearchResults(doubanApi, "Legacy TMDb Movie", Array.Empty<DoubanSubject>());
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbMovieSearchResults(
                    tmdbApi,
                    "Legacy TMDb Movie",
                    0,
                    "zh-CN",
                    new SearchMovie
                    {
                        Id = 9305,
                        Title = "Legacy TMDb Movie",
                        OriginalTitle = "TMDb Legacy MetaSharkID Movie",
                        ReleaseDate = new DateTime(2024, 5, 3),
                    });
                SeedTmdbMovie(
                    tmdbApi,
                    1111,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 1111,
                        Title = "错误 legacy MetaSharkID 电影",
                        OriginalTitle = "Wrong Legacy MetaSharkID Movie",
                        ImdbId = "tt0001111",
                        Overview = "如果复用 MetaSharkID=Tmdb_1111 就会得到这段错误简介",
                        Tagline = "wrong legacy MetaSharkID tagline",
                        ReleaseDate = new DateTime(2024, 1, 1),
                        VoteAverage = 1.1,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });
                SeedTmdbMovie(
                    tmdbApi,
                    9305,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 9305,
                        Title = "TMDb legacy MetaSharkID 电影",
                        OriginalTitle = "TMDb Legacy MetaSharkID Movie",
                        ImdbId = "tt0009305",
                        Overview = "TMDb legacy MetaSharkID 默认兜底简介",
                        Tagline = "TMDb legacy MetaSharkID fallback tagline",
                        ReleaseDate = new DateTime(2024, 5, 3),
                        VoteAverage = 7.4,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new MovieInfo
                {
                    Name = "Legacy TMDb Movie",
                    Path = "/library/movies/Legacy TMDb Movie/Legacy TMDb Movie.mkv",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [MetaSharkPlugin.ProviderId] = "Tmdb_1111",
                    },
                };
                var currentMovie = new Movie
                {
                    Id = Guid.NewGuid(),
                    Path = info.Path,
                };

                var libraryManagerStub = new Mock<ILibraryManager>();
                libraryManagerStub
                    .Setup(x => x.FindByPath(info.Path, false))
                    .Returns(currentMovie);

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = null },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "default 下缺少官方 TMDb 时应先尝试 Douban；Douban 未命中后必须按标题搜索 TMDb，而不是复用 MetaSharkID=Tmdb_*。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("9305", result.Item!.GetProviderId(MetadataProvider.Tmdb), "旧 MetaSharkID=Tmdb_1111 不能作为有效 TMDb id；搜索命中的 9305 必须写成官方 TMDb provider id。 ");
                Assert.AreEqual("Tmdb_9305", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId), "legacy TMDb 兜底不应把 default 变成沿用旧 Douban 元数据。 ");
                Assert.AreEqual("TMDb legacy MetaSharkID 电影", result.Item.Name);
                Assert.AreEqual("TMDb legacy MetaSharkID 默认兜底简介", result.Item.Overview);
            }
            finally
            {
                configuration.DefaultScraperMode = originalMode;
                configuration.EnableTmdb = originalEnableTmdb;
                configuration.EnableTmdbMatch = originalEnableTmdbMatch;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldSearchAndWriteStandardTmdbId_WhenOnlyHistoricalMetaSharkTmdbIdExists()
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
                SeedDoubanSearchResults(doubanApi, "Historical Legacy TMDb Movie", Array.Empty<DoubanSubject>());
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbMovieSearchResults(
                    tmdbApi,
                    "Historical Legacy TMDb Movie",
                    0,
                    "zh-CN",
                    new SearchMovie
                    {
                        Id = 9307,
                        Title = "Historical Legacy TMDb Movie",
                        OriginalTitle = "TMDb Historical Legacy Movie",
                        ReleaseDate = new DateTime(2024, 5, 4),
                    });
                SeedTmdbMovie(
                    tmdbApi,
                    2222,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 2222,
                        Title = "错误 historical legacy 电影",
                        OriginalTitle = "Wrong Historical Legacy Movie",
                        ImdbId = "tt0002222",
                        Overview = "如果复用 MetaSharkTmdbID=2222 就会得到这段错误简介",
                        Tagline = "wrong historical legacy tagline",
                        ReleaseDate = new DateTime(2024, 1, 2),
                        VoteAverage = 1.2,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });
                SeedTmdbMovie(
                    tmdbApi,
                    9307,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 9307,
                        Title = "TMDb historical legacy 电影",
                        OriginalTitle = "TMDb Historical Legacy Movie",
                        ImdbId = "tt0009307",
                        Overview = "TMDb historical legacy 默认兜底简介",
                        Tagline = "TMDb historical legacy fallback tagline",
                        ReleaseDate = new DateTime(2024, 5, 4),
                        VoteAverage = 7.5,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new MovieInfo
                {
                    Name = "Historical Legacy TMDb Movie",
                    Path = "/library/movies/Historical Legacy TMDb Movie/Historical Legacy TMDb Movie.mkv",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        ["MetaSharkTmdbID"] = "2222",
                    },
                };
                var currentMovie = new Movie
                {
                    Id = Guid.NewGuid(),
                    Path = info.Path,
                };

                var libraryManagerStub = new Mock<ILibraryManager>();
                libraryManagerStub
                    .Setup(x => x.FindByPath(info.Path, false))
                    .Returns(currentMovie);

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = null },
                    tmdbApi,
                    store,
                    loggerFactory,
                    doubanApi);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item, "default 下缺少官方 TMDb 时应先尝试 Douban；Douban 未命中后必须按标题搜索 TMDb，而不是复用 MetaSharkTmdbID。 ");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("9307", result.Item!.GetProviderId(MetadataProvider.Tmdb), "旧 MetaSharkTmdbID=2222 不能作为有效 TMDb id；搜索命中的 9307 必须写成官方 TMDb provider id。 ");
                Assert.AreEqual("Tmdb_9307", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.IsNull(result.Item.GetProviderId("MetaSharkTmdbID"), "成功 TMDb 搜索匹配后不应写回旧 MetaSharkTmdbID。 ");
                Assert.AreEqual("TMDb historical legacy 电影", result.Item.Name);
                Assert.AreEqual("TMDb historical legacy 默认兜底简介", result.Item.Overview);
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
                    Sid = "wrong-douban-legacy-tmdb-movie",
                    Name = "错误豆瓣 legacy 电影",
                    OriginalName = "Wrong Douban Legacy TMDb Movie",
                    Year = 0,
                    Category = "电影",
                    Rating = 1.1f,
                    Genre = "短片",
                    Intro = "default 配置下应保留 Douban 先行，即使这个候选不理想",
                    Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000401.webp",
                };
                SeedDoubanSearchResults(doubanApi, "Legacy TMDb Movie", new[] { wrongDoubanSubject });
                SeedDoubanSubject(doubanApi, wrongDoubanSubject);
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbMovieSearchResults(
                    tmdbApi,
                    "Legacy TMDb Movie",
                    0,
                    "zh-CN",
                    new SearchMovie
                    {
                        Id = 9305,
                        Title = "Legacy TMDb Movie",
                        OriginalTitle = "TMDb Legacy MetaSharkID Movie",
                        ReleaseDate = new DateTime(2024, 5, 3),
                    });
                SeedTmdbMovie(
                    tmdbApi,
                    9305,
                    "zh-CN",
                    new TmdbMovie
                    {
                        Id = 9305,
                        Title = "TMDb legacy MetaSharkID 电影",
                        OriginalTitle = "TMDb Legacy MetaSharkID Movie",
                        ImdbId = "tt0009305",
                        Overview = "如果绕过 Douban 就会得到这段 TMDb 简介",
                        Tagline = "TMDb fallback tagline",
                        ReleaseDate = new DateTime(2024, 5, 3),
                        VoteAverage = 7.4,
                        ProductionCountries = new List<ProductionCountry>(),
                        Genres = new List<TmdbGenre>(),
                    });

                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = new MovieInfo
                {
                    Name = "Legacy TMDb Movie",
                    Path = "/library/movies/Legacy TMDb Movie/Legacy TMDb Movie.mkv",
                    MetadataLanguage = "zh-CN",
                    IsAutomated = false,
                    ProviderIds = new Dictionary<string, string>
                    {
                        [MetaSharkPlugin.ProviderId] = "Tmdb_1111",
                    },
                };
                var currentMovie = new Movie
                {
                    Id = Guid.NewGuid(),
                    Path = info.Path,
                };

                var libraryManagerStub = new Mock<ILibraryManager>();
                libraryManagerStub
                    .Setup(x => x.FindByPath(info.Path, false))
                    .Returns(currentMovie);

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
                Assert.AreEqual("wrong-douban-legacy-tmdb-movie", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
                Assert.AreEqual("Douban_wrong-douban-legacy-tmdb-movie", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.IsNull(result.Item.GetProviderId(MetadataProvider.Tmdb), "MetaSharkID=Tmdb_1111 不能被解析成官方 TMDb id。 ");
                Assert.AreEqual("错误豆瓣 legacy 电影", result.Item.Name);
                Assert.AreEqual("default 配置下应保留 Douban 先行，即使这个候选不理想", result.Item.Overview);
                Assert.AreNotEqual("TMDb legacy MetaSharkID 电影", result.Item.Name);
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
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateMovieInfo();
            info.IsAutomated = false;
            var currentMovie = new Movie
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(currentMovie);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                CreateManualMatchContextAccessor(),
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            var candidate = store.Peek(currentMovie.Id);
            Assert.IsNotNull(candidate, "ManualMatch /Items/RemoteSearch/Apply 命中 TMDb provider 时，应创建单项 overwrite candidate。 ");
            Assert.AreEqual(currentMovie.Id, candidate!.ItemId);
            Assert.AreEqual(info.Path, candidate.ItemPath);
            Assert.AreEqual(result.People?.Count ?? 0, candidate.ExpectedPeopleCount);
            AssertAuthoritativeSnapshot(candidate, nameof(Movie), "123", result.People);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenTmdbPeopleIsEmptyButCurrentMovieStillHasPeople()
        {
            EnsurePluginInstance();
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            Assert.IsNotNull(plugin!.Configuration);

            var originalMode = plugin.Configuration.DefaultScraperMode;

            try
            {
                plugin.Configuration.DefaultScraperMode = PluginConfiguration.DefaultScraperModeTmdbOnly;

                using var loggerFactory = LoggerFactory.Create(builder => { });
                var tmdbApi = new TmdbApi(loggerFactory);
                SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");
                var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
                var info = CreateMovieInfo();
                info.IsAutomated = false;
                var currentMovie = new AuthoritativeTrackingMovie
                {
                    Id = Guid.NewGuid(),
                    Path = info.Path,
                };
                currentMovie.SetProviderId(MetadataProvider.Tmdb, "123");
                currentMovie.SetSimulatedPeople(new[]
                {
                    CreateCurrentPerson("旧演员甲", "角色甲", PersonKind.Actor),
                    CreateCurrentPerson("旧导演甲", "Director", PersonKind.Director),
                });

                var libraryManagerStub = new Mock<ILibraryManager>();
                libraryManagerStub
                    .Setup(x => x.FindByPath(info.Path, false))
                    .Returns(currentMovie);

                var provider = CreateProvider(
                    libraryManagerStub.Object,
                    new HttpContextAccessor { HttpContext = null },
                    tmdbApi,
                    store,
                    loggerFactory);

                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsNotNull(result.Item);
                Assert.AreEqual(0, result.People?.Count ?? 0, "测试前提：TMDb authoritative 应为空。 ");
                Assert.IsNull(store.Peek(currentMovie.Id), "tmdb-only UserRefresh 不应保存空 authoritative candidate。 ");
            }
            finally
            {
                plugin.Configuration.DefaultScraperMode = originalMode;
            }
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenCurrentMovieLookupMisses()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateMovieInfo();
            info.IsAutomated = false;
            var currentMovieId = Guid.NewGuid();

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentMovieId) },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.IsNull(store.Peek(currentMovieId), "UserRefresh 不应因为请求路径回退而保存 overwrite candidate。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotResetQueuedCandidate_WhenExistingCandidateAlreadyPending()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateMovieInfo();
            info.IsAutomated = true;
            var currentMovie = new Movie
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            store.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = currentMovie.Id,
                ItemPath = currentMovie.Path,
                ExpectedPeopleCount = 17,
                OverwriteQueued = true,
            });

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(currentMovie);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = null },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            var candidate = store.Peek(currentMovie.Id);
            Assert.IsNotNull(candidate, "follow-up overwrite refresh 期间不应把已排队 candidate 清掉或重置。 ");
            Assert.AreEqual(17, candidate!.ExpectedPeopleCount, "已排队 candidate 不应被 provider 新写入的未排队 candidate 覆盖。 ");
            Assert.IsTrue(candidate.OverwriteQueued, "已排队 candidate 不应被重置为未排队。 ");
            Assert.AreEqual(currentMovie.Path, candidate.ItemPath);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldKeepQueuedCandidate_WhenExplicitRefreshRequestWithoutReplaceAllMetadata()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateMovieInfo();
            info.IsAutomated = false;
            var currentMovie = new Movie
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            store.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = currentMovie.Id,
                ItemPath = currentMovie.Path,
                ExpectedPeopleCount = 17,
                OverwriteQueued = true,
            });

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(currentMovie);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentMovie.Id, replaceAllMetadata: false) },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            var candidate = store.Peek(currentMovie.Id);
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
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateMovieInfo();
            info.IsAutomated = false;
            var currentMovie = new Movie
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            store.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = currentMovie.Id,
                ItemPath = currentMovie.Path,
                ExpectedPeopleCount = 17,
                OverwriteQueued = true,
            });

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(currentMovie);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = CreateRefreshRequestContext(currentMovie.Id, replaceAllMetadata: true) },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            var candidate = store.Peek(currentMovie.Id);
            Assert.IsNotNull(candidate, "ReplaceAllMetadata=true 的 overwrite refresh 不应打破现有 queued candidate 幂等性。 ");
            Assert.AreEqual(17, candidate!.ExpectedPeopleCount, "overwrite refresh 不应被新的未排队 candidate 覆盖。 ");
            Assert.IsTrue(candidate.OverwriteQueued, "ReplaceAllMetadata=true 时，queued candidate 仍应保持已排队状态。 ");
            Assert.AreEqual(currentMovie.Path, candidate.ItemPath);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldKeepQueuedCandidate_WhenUserRefreshRunsWithoutHttpContext()
        {
            EnsurePluginInstance();

            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 123, "zh-CN", "示例电影");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateMovieInfo();
            info.IsAutomated = false;
            var currentMovie = new Movie
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            store.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = currentMovie.Id,
                ItemPath = currentMovie.Path,
                ExpectedPeopleCount = 17,
                OverwriteQueued = true,
            });

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(currentMovie);

            var provider = CreateProvider(
                libraryManagerStub.Object,
                new HttpContextAccessor { HttpContext = null },
                tmdbApi,
                store,
                loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            var candidate = store.Peek(currentMovie.Id);
            Assert.IsNotNull(candidate, "UserRefresh 在没有 HttpContext 时也不应改写已排队的 queued candidate。 ");
            Assert.AreEqual(17, candidate!.ExpectedPeopleCount, "无 HttpContext 的 UserRefresh 不应覆盖已有 candidate 的 expected people 数。 ");
            Assert.IsTrue(candidate.OverwriteQueued, "无 HttpContext 的 UserRefresh 下 queued candidate 应保持已排队状态。 ");
            Assert.IsNull(candidate.AuthoritativePeopleSnapshot, "无 HttpContext 的 UserRefresh 不应重写 queued candidate 的 authoritative 快照。 ");
        }

        private static MovieProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IMovieSeriesPeopleOverwriteRefreshCandidateStore store,
            ILoggerFactory loggerFactory,
            DoubanApi? doubanApi = null)
        {
            return new MovieProvider(
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

        private static MovieInfo CreateMovieInfo()
        {
            return new MovieInfo
            {
                Name = "示例电影",
                Path = "/library/movies/sample-movie/sample-movie.mkv",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    [MetaSharkPlugin.ProviderId] = "Tmdb_123",
                    [MetadataProvider.Tmdb.ToString()] = "123",
                },
            };
        }

        private static DefaultHttpContext CreateRefreshRequestContext(Guid itemId, bool? replaceAllMetadata = null, string? metadataRefreshMode = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/Items/{itemId:N}/Refresh";
            if (replaceAllMetadata.HasValue)
            {
                var queryString = $"?replaceAllMetadata={replaceAllMetadata.Value.ToString().ToLowerInvariant()}";
                if (!string.IsNullOrWhiteSpace(metadataRefreshMode))
                {
                    queryString += $"&metadataRefreshMode={metadataRefreshMode}";
                }

                context.Request.QueryString = new QueryString(queryString);
            }

            return context;
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

        private static void SeedTmdbMovie(TmdbApi tmdbApi, int tmdbId, string language, string title)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"movie-{tmdbId}-{language}-{language}",
                new TmdbMovie
                {
                    Id = tmdbId,
                    Title = title,
                    OriginalTitle = title,
                    ImdbId = "tt0000001",
                    Overview = "TMDb seeded movie overview",
                    Tagline = "TMDb seeded movie tagline",
                    ReleaseDate = new DateTime(2007, 3, 3),
                    VoteAverage = 8.6,
                    ProductionCountries = new List<ProductionCountry>(),
                    Genres = new List<TmdbGenre>(),
                },
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbMovie(TmdbApi tmdbApi, int tmdbId, string language, TmdbMovie movie)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"movie-{tmdbId}-{language}-{language}",
                movie,
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbMovieSearchResults(TmdbApi tmdbApi, string name, int year, string language, params SearchMovie[] results)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"moviesearch-{name}-{year}-{language}",
                new SearchContainer<SearchMovie>
                {
                    Results = results.ToList(),
                },
                TimeSpan.FromMinutes(5));
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

        private static void SetTmdbMovieCredits(
            TmdbMovie movie,
            IEnumerable<Dictionary<string, object?>> castEntries,
            IEnumerable<Dictionary<string, object?>> crewEntries)
        {
            var creditsProperty = typeof(TmdbMovie).GetProperty("Credits");
            Assert.IsNotNull(creditsProperty, "TMDb movie Credits 属性未定义");

            var credits = Activator.CreateInstance(creditsProperty!.PropertyType);
            Assert.IsNotNull(credits, "无法创建 TMDb movie Credits 实例");

            SetTmdbCreditList(credits!, "Cast", castEntries);
            SetTmdbCreditList(credits!, "Crew", crewEntries);
            creditsProperty.SetValue(movie, credits);
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

        private static Dictionary<string, object?> CreateCastCredit(int tmdbId, string rawName, string character, int order, string? profilePath = null)
        {
            var entry = new Dictionary<string, object?>
            {
                ["Id"] = tmdbId,
                ["Name"] = rawName,
                ["Character"] = character,
                ["Order"] = order,
            };

            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                entry["ProfilePath"] = profilePath;
            }

            return entry;
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

        private static MemoryCache GetDoubanMemoryCache(DoubanApi doubanApi)
        {
            var memoryCacheField = typeof(DoubanApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "DoubanApi.memoryCache 未找到");

            var memoryCache = memoryCacheField!.GetValue(doubanApi) as MemoryCache;
            Assert.IsNotNull(memoryCache, "DoubanApi.memoryCache 不是有效的 MemoryCache");
            return memoryCache!;
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

        private static void SeedBlankExactZhCnPerson(TmdbApi tmdbApi, int tmdbId)
        {
            SeedTmdbPerson(tmdbApi, tmdbId, string.Empty, language: "zh-CN");
            SeedTmdbPersonTranslations(tmdbApi, tmdbId, CreatePersonTranslation("zh", "CN", string.Empty));
        }

        private static void SeedTmdbPersonTranslations(TmdbApi tmdbApi, int tmdbId, params TMDbLib.Objects.General.Translation[] translations)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                GetTmdbPersonTranslationsCacheKey(tmdbId),
                new TMDbLib.Objects.General.TranslationsContainer
                {
                    Id = tmdbId,
                    Translations = translations.ToList(),
                },
                TimeSpan.FromMinutes(5));
        }

        private static TMDbLib.Objects.General.Translation CreatePersonTranslation(string language, string? locale, string? translatedName)
        {
            return new TMDbLib.Objects.General.Translation
            {
                Iso_639_1 = language,
                Iso_3166_1 = locale,
                Data = new TmdbTranslationData
                {
                    Name = translatedName,
                },
            };
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
