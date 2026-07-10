using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using TMDbLib.Objects.Collections;
using TMDbLib.Objects.Find;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Movies;
using TMDbLib.Objects.Search;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class TmdbApiChineseLocaleTest
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "metashark-tmdb-chinese-locale-tests");
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestInitialize]
        public void Initialize()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void Cleanup()
        {
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [DataTestMethod]
        [DataRow("zh", "TW", "zh-CN", "zh-TW")]
        [DataRow("zh", "AU", "zh-HK", "zh-HK")]
        [DataRow("zh-Hant", "HK", "zh-CN", "zh-HK")]
        [DataRow("zh-Hans", "SG", "zh-TW", "zh-SG")]
        [DataRow("en-us", "CN", "zh-CN", "en-US")]
        public void ResolveMetadataLanguage_UsesCountryAndConfiguredDefault(string language, string? countryCode, string configuredDefault, string expected)
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = configuredDefault,
            });
            using var api = new TmdbApi(this.loggerFactory);

            Assert.AreEqual(expected, api.ResolveMetadataLanguage(language, countryCode));
        }

        [TestMethod]
        public void ResolveMetadataLanguage_UsesConfigurationSavedAfterApiConstruction()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = "zh-CN",
            });
            using var api = new TmdbApi(this.loggerFactory);

            Assert.AreEqual("zh-CN", api.ResolveMetadataLanguage("zh"));

            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = "zh-TW",
            });

            Assert.AreEqual("zh-TW", api.ResolveMetadataLanguage("zh"));
        }

        [TestMethod]
        public async Task GenericChineseDetailCaches_UseLiveResolvedLocale()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = "zh-CN",
            });
            using var api = new TmdbApi(this.loggerFactory);
            var cache = GetTmdbMemoryCache(api);
            cache.Set("movie-42-zh-zh", new Movie { Id = 42, Title = "错误的旧地区电影名" });
            cache.Set("movie-42-zh-TW-zh", new Movie { Id = 42, Title = "正確的繁體電影名" });
            cache.Set("series-114410-zh-zh", new TvShow { Id = 114410, Name = "错误的旧地区剧名" });
            cache.Set("series-114410-zh-TW-zh", new TvShow { Id = 114410, Name = "鏈鋸人" });

            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = "zh-TW",
            });

            var movie = await api.GetMovieAsync(42, "zh", "zh", CancellationToken.None).ConfigureAwait(false);
            var series = await api.GetSeriesAsync(114410, "zh", "zh", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("正確的繁體電影名", movie?.Title);
            Assert.AreEqual("鏈鋸人", series?.Name);
        }

        [TestMethod]
        public async Task GenericChineseLookupCaches_UseLiveResolvedLocale()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = "zh-CN",
            });
            using var api = new TmdbApi(this.loggerFactory);
            var cache = GetTmdbMemoryCache(api);
            cache.Set("searchseries-鏈鋸人-zh", new SearchContainer<SearchTv>
            {
                Results = new List<SearchTv> { new SearchTv { Id = 1, Name = "错误的旧地区剧名" } },
            });
            cache.Set("searchseries-鏈鋸人-zh-TW", new SearchContainer<SearchTv>
            {
                Results = new List<SearchTv> { new SearchTv { Id = 2, Name = "鏈鋸人" } },
            });
            cache.Set("moviesearch-鏈鋸人-0-zh", new SearchContainer<SearchMovie>
            {
                Results = new List<SearchMovie> { new SearchMovie { Id = 3, Title = "错误的旧地区电影名" } },
            });
            cache.Set("moviesearch-鏈鋸人-0-zh-TW", new SearchContainer<SearchMovie>
            {
                Results = new List<SearchMovie> { new SearchMovie { Id = 4, Title = "正確的繁體電影名" } },
            });
            cache.Set("collection-5-zh-zh", new Collection { Id = 5, Name = "错误的旧地区合集名" });
            cache.Set("collection-5-zh-TW-zh", new Collection { Id = 5, Name = "正確的繁體合集名" });
            cache.Set("collectionsearch-鏈鋸人-zh", new SearchContainer<SearchCollection>
            {
                Results = new List<SearchCollection> { new SearchCollection { Id = 6, Name = "错误的旧地区合集搜索名" } },
            });
            cache.Set("collectionsearch-鏈鋸人-zh-TW", new SearchContainer<SearchCollection>
            {
                Results = new List<SearchCollection> { new SearchCollection { Id = 7, Name = "正確的繁體合集搜索名" } },
            });
            var wrongFind = new FindContainer();
            var rightFind = new FindContainer();
            cache.Set("find-Imdb-tt0000042-zh", wrongFind);
            cache.Set("find-Imdb-tt0000042-zh-TW", rightFind);

            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = "zh-TW",
            });

            var seriesSearch = await api.SearchSeriesAsync("鏈鋸人", "zh", CancellationToken.None).ConfigureAwait(false);
            var movieSearch = await api.SearchMovieAsync("鏈鋸人", "zh", CancellationToken.None).ConfigureAwait(false);
            var collection = await api.GetCollectionAsync(5, "zh", "zh", CancellationToken.None).ConfigureAwait(false);
            var collectionSearch = await api.SearchCollectionAsync("鏈鋸人", "zh", CancellationToken.None).ConfigureAwait(false);
            var find = await api.FindByExternalIdAsync("tt0000042", FindExternalSource.Imdb, "zh", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("鏈鋸人", seriesSearch.Single().Name);
            Assert.AreEqual("正確的繁體電影名", movieSearch.Single().Title);
            Assert.AreEqual("正確的繁體合集名", collection?.Name);
            Assert.AreEqual("正確的繁體合集搜索名", collectionSearch.Single().Name);
            Assert.AreSame(rightFind, find);
        }

        [TestMethod]
        public async Task GenericChineseSubresourceCaches_UseLiveResolvedLocale()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = "zh-CN",
            });
            using var api = new TmdbApi(this.loggerFactory);
            var cache = GetTmdbMemoryCache(api);
            var wrongMovieImages = new ImagesWithId();
            var rightMovieImages = new ImagesWithId();
            var wrongSeriesImages = new ImagesWithId();
            var rightSeriesImages = new ImagesWithId();
            var wrongGroup = new TvGroupCollection();
            var rightGroup = new TvGroupCollection();
            var wrongGroupById = new TvGroupCollection();
            var rightGroupById = new TvGroupCollection();
            var wrongSeason = new TvSeason();
            var rightSeason = new TvSeason();
            var wrongEpisode = new TvEpisode { StillPath = "/wrong.jpg" };
            var rightEpisode = new TvEpisode { StillPath = "/right.jpg" };
            var wrongEpisodeImages = new StillImages();
            var rightEpisodeImages = new StillImages();
            cache.Set("movie-images-42-zh-zh", wrongMovieImages);
            cache.Set("movie-images-42-zh-TW-zh", rightMovieImages);
            cache.Set("series-images-114410-zh-zh", wrongSeriesImages);
            cache.Set("series-images-114410-zh-TW-zh", rightSeriesImages);
            cache.Set("group-114410-originalAirDate-zh", wrongGroup);
            cache.Set("group-114410-originalAirDate-zh-TW", rightGroup);
            cache.Set("group-id-group-42-zh", wrongGroupById);
            cache.Set("group-id-group-42-zh-TW", rightGroupById);
            cache.Set("season-114410-s1-zh-zh", wrongSeason);
            cache.Set("season-114410-s1-zh-TW-zh", rightSeason);
            cache.Set("episode-114410-s1e1-zh-zh", wrongEpisode);
            cache.Set("episode-114410-s1e1-zh-TW-zh", rightEpisode);
            cache.Set("episode-images-114410-s1e1-zh-zh", wrongEpisodeImages);
            cache.Set("episode-images-114410-s1e1-zh-TW-zh", rightEpisodeImages);

            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = "zh-TW",
            });

            Assert.AreSame(rightMovieImages, await api.GetMovieImagesAsync(42, "zh", "zh", CancellationToken.None).ConfigureAwait(false));
            Assert.AreSame(rightSeriesImages, await api.GetSeriesImagesAsync(114410, "zh", "zh", CancellationToken.None).ConfigureAwait(false));
            Assert.AreSame(rightGroup, await api.GetSeriesGroupAsync(114410, "originalAirDate", "zh", "zh", CancellationToken.None).ConfigureAwait(false));
            Assert.AreSame(rightGroupById, await api.GetEpisodeGroupByIdAsync("group-42", "zh", CancellationToken.None).ConfigureAwait(false));
            Assert.AreSame(rightSeason, await api.GetSeasonAsync(114410, 1, "zh", "zh", CancellationToken.None).ConfigureAwait(false));
            Assert.AreSame(rightEpisode, await api.GetEpisodeAsync(114410, 1, 1, "zh", "zh", CancellationToken.None).ConfigureAwait(false));
            Assert.AreSame(rightEpisodeImages, await api.GetEpisodeImagesAsync(114410, 1, 1, "zh", "zh", CancellationToken.None).ConfigureAwait(false));
        }

        [TestMethod]
        public void ImageLanguages_KeepGenericZhCompatibility()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = "zh-TW",
            });
            var method = typeof(TmdbApi).GetMethod("GetImageLanguagesParam", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            var result = method!.Invoke(null, new object[] { "zh" }) as string;

            Assert.AreEqual("zh,null,en", result);
        }

        private static void EnsurePluginInstance()
        {
            if (MetaSharkPlugin.Instance != null)
            {
                return;
            }

            var pluginsPath = Path.Combine(Root, "plugins");
            var configurationsPath = Path.Combine(Root, "configurations");
            Directory.CreateDirectory(pluginsPath);
            Directory.CreateDirectory(configurationsPath);
            var appHost = new Mock<IServerApplicationHost>();
            appHost.Setup(x => x.GetLocalApiUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
                .Returns("http://127.0.0.1:8096");
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(x => x.PluginsPath).Returns(pluginsPath);
            paths.SetupGet(x => x.PluginConfigurationsPath).Returns(configurationsPath);
            _ = new MetaSharkPlugin(appHost.Object, paths.Object, new Mock<IXmlSerializer>().Object);
        }

        private static IMemoryCache GetTmdbMemoryCache(TmdbApi api)
        {
            var field = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            var cache = field!.GetValue(api) as IMemoryCache;
            Assert.IsNotNull(cache);
            return cache!;
        }

        private static void ReplacePluginConfiguration(PluginConfiguration configuration)
        {
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            var type = plugin!.GetType();
            while (type != null)
            {
                var property = type.GetProperty("Configuration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.SetMethod != null && property.PropertyType.IsAssignableFrom(typeof(PluginConfiguration)))
                {
                    property.SetValue(plugin, configuration);
                    return;
                }

                var field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => candidate.FieldType.IsAssignableFrom(typeof(PluginConfiguration)));
                if (field != null)
                {
                    field.SetValue(plugin, configuration);
                    return;
                }

                type = type.BaseType;
            }

            Assert.Fail("Could not replace plugin configuration.");
        }
    }
}
