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

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class MovieProviderSearchMissingMetadataRequestTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-movie-provider-request-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        [TestMethod]
        public async Task GetMetadata_ShouldSaveCandidate_WhenInfoRepresentsUserRefresh()
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
            var candidate = store.Peek(currentMovie.Id);
            Assert.IsNotNull(candidate, "手动单项电影 refresh 命中 TMDb provider 时，应保存一次性 overwrite candidate。 ");
            Assert.AreEqual(currentMovie.Id, candidate!.ItemId);
            Assert.AreEqual(info.Path, candidate.ItemPath);
            Assert.AreEqual(result.People?.Count ?? 0, candidate.ExpectedPeopleCount, "candidate 应记录 provider 实际产出的期望 people 数。 ");
            AssertAuthoritativeSnapshot(candidate, nameof(Movie), "123", result.People);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldSaveAcceptedTmdbPeopleCount_WhenDoubanBranchBackfillsPeopleForUserRefresh()
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
            Assert.AreEqual(2, result.People.Count, "Douban 主分支命中 tmdbId 时，应只统计并写入实际 accepted 的 TMDb people。 ");

            var candidate = store.Peek(currentMovie.Id);
            Assert.IsNotNull(candidate, "Douban 主分支在手动单项 refresh 中产出 TMDb people 时，应保存一次性 overwrite candidate。 ");
            Assert.AreEqual(currentMovie.Id, candidate!.ItemId);
            Assert.AreEqual(info.Path, candidate.ItemPath);
            Assert.AreEqual(result.People.Count, candidate.ExpectedPeopleCount, "candidate 应记录 Douban 主分支实际接受的 TMDb people 数，而不是原始 credits 数。 ");
            AssertAuthoritativeSnapshot(candidate, nameof(Movie), "123", result.People);
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
            Assert.IsNull(store.Peek(currentMovie.Id), "自动刷新不应为单项 one-shot overwrite 保存 candidate。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldSaveCandidate_ForManualMatchRequest()
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
            Assert.IsNotNull(candidate, "手动匹配 /Items/RemoteSearch/Apply 命中 TMDb provider 时，也应创建单项 overwrite candidate。 ");
            Assert.AreEqual(currentMovie.Id, candidate!.ItemId);
            Assert.AreEqual(info.Path, candidate.ItemPath);
            Assert.AreEqual(result.People?.Count ?? 0, candidate.ExpectedPeopleCount);
            AssertAuthoritativeSnapshot(candidate, nameof(Movie), "123", result.People);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldSaveEmptyAuthoritativeCandidate_WhenTmdbPeopleIsEmptyButCurrentMovieStillHasPeople()
        {
            EnsurePluginInstance();

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
            var candidate = store.Peek(currentMovie.Id);
            Assert.IsNotNull(candidate, "TMDb authoritative 为空但当前电影仍有旧 people 时，也应保存待清理 candidate。 ");
            Assert.AreEqual(currentMovie.Id, candidate!.ItemId);
            Assert.AreEqual(info.Path, candidate.ItemPath);
            Assert.AreEqual(0, candidate.ExpectedPeopleCount, "空 authoritative candidate 仍应保留 0 的期望人数，兼容当前消费方。 ");
            AssertAuthoritativeSnapshot(candidate, nameof(Movie), "123", Array.Empty<PersonInfo>());
            Assert.IsFalse(CurrentItemAuthoritativePeopleChecker.IsAuthoritativeEmpty(currentMovie, candidate.AuthoritativePeopleSnapshot), "当前条目仍残留旧人物时，空 authoritative 快照不应被误判成 authoritative empty。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldUseRequestPathFallback_WhenCurrentMovieLookupMisses()
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
            var candidate = store.Peek(currentMovieId);
            Assert.IsNotNull(candidate, "FindByPath 没拿到当前电影时，应回退到请求路径中的 item id 保存 candidate。 ");
            Assert.AreEqual(currentMovieId, candidate!.ItemId);
            Assert.AreEqual(info.Path, candidate.ItemPath);
            Assert.AreEqual(result.People?.Count ?? 0, candidate.ExpectedPeopleCount, "路径回退命中时也应保留 provider 实际产出的期望 people 数。 ");
            AssertAuthoritativeSnapshot(candidate, nameof(Movie), "123", result.People);
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
        public async Task GetMetadata_ShouldRearmQueuedCandidate_WhenExplicitRefreshRequestWithoutReplaceAllMetadata()
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
            Assert.IsNotNull(candidate, "显式用户 refresh 且 ReplaceAllMetadata=false 时，应允许重新武装已锁死的 queued candidate。 ");
            Assert.AreEqual(result.People?.Count ?? 0, candidate!.ExpectedPeopleCount, "重新武装后的 candidate 应反映本次 provider 实际产出的 people 数。 ");
            Assert.IsFalse(candidate.OverwriteQueued, "显式用户 refresh 重新武装时，应把 queued candidate 还原成未排队状态，交给 post-process 再次决定是否 queue。 ");
            AssertAuthoritativeSnapshot(candidate, nameof(Movie), "123", result.People);
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
        public async Task GetMetadata_ShouldRearmQueuedCandidate_WhenUserRefreshRunsWithoutHttpContext()
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
            Assert.IsNotNull(candidate, "Jellyfin 后台执行手动电影 refresh 时，即使没有 HttpContext，也应允许重新武装已锁死的 queued candidate。 ");
            Assert.AreEqual(result.People?.Count ?? 0, candidate!.ExpectedPeopleCount, "无 HttpContext 的手动 refresh 重新武装后，也应反映本次 provider 实际产出的 people 数。 ");
            Assert.IsFalse(candidate.OverwriteQueued, "无 HttpContext 的手动 refresh 重新武装时，应把 queued candidate 还原成未排队状态。 ");
            AssertAuthoritativeSnapshot(candidate, nameof(Movie), "123", result.People);
        }

        private static MovieProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IMovieSeriesPeopleOverwriteRefreshCandidateStore store,
            ILoggerFactory loggerFactory)
        {
            return new MovieProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager,
                httpContextAccessor,
                new DoubanApi(loggerFactory),
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

        private static DefaultHttpContext CreateRefreshRequestContext(Guid itemId, bool? replaceAllMetadata = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/Items/{itemId:N}/Refresh";
            if (replaceAllMetadata.HasValue)
            {
                context.Request.QueryString = new QueryString($"?ReplaceAllMetadata={replaceAllMetadata.Value.ToString().ToLowerInvariant()}");
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
