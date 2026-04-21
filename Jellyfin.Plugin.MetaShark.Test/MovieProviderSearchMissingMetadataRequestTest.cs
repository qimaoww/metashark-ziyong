using System.Reflection;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TmdbMovie = TMDbLib.Objects.Movies.Movie;
using TmdbGenre = TMDbLib.Objects.General.Genre;
using ProductionCountry = TMDbLib.Objects.General.ProductionCountry;

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
        public async Task GetMetadata_ShouldNotSaveCandidate_ForManualMatchRequest()
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
            Assert.IsNull(store.Peek(currentMovie.Id), "手动匹配 /Items/RemoteSearch/Apply 不应创建单项 overwrite candidate。 ");
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

        private static DefaultHttpContext CreateRefreshRequestContext(Guid itemId)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/Items/{itemId:N}/Refresh";

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
