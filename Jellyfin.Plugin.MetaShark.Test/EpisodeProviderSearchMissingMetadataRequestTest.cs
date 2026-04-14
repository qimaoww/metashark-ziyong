using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
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
using System.Reflection;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeProviderSearchMissingMetadataRequestTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-provider-request-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestCleanup]
        public void Cleanup()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = false;
        }

        [DataTestMethod]
        [DataRow("FullRefresh", "false", true)]
        [DataRow("fullrefresh", "FALSE", true)]
        [DataRow("FullRefresh", "true", false)]
        [DataRow("Default", "false", false)]
        [DataRow(null, "false", false)]
        [DataRow("FullRefresh", null, false)]
        public void ShouldRecognizeOnlySearchMissingMetadataRefreshMode(string? metadataRefreshMode, string? replaceAllMetadata, bool expected)
        {
            var result = InvokeIsSearchMissingMetadataRefresh(metadataRefreshMode, replaceAllMetadata);

            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldSaveCandidate_WhenRequestMatchesSearchMissingMode()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            EpisodeTitleBackfillCandidate? savedCandidate = null;
            storeStub
                .Setup(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()))
                .Callback<EpisodeTitleBackfillCandidate>(candidate => savedCandidate = candidate);

            var info = CreateEpisodeInfo();
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "  皇后回宫  ",
            });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object);

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Once);
            libraryManagerStub.Verify(x => x.FindByPath(info.Path, false), Times.AtLeastOnce);
            Assert.IsNotNull(savedCandidate);
            Assert.AreEqual(episodeItem.Id, savedCandidate!.ItemId);
            Assert.AreEqual("第 1 集", savedCandidate.OriginalTitleSnapshot);
            Assert.AreEqual("皇后回宫", savedCandidate.CandidateTitle);
            Assert.AreEqual(TimeSpan.FromMinutes(10), savedCandidate.ExpiresAtUtc - savedCandidate.CreatedAtUtc);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenNoHttpContext()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var episodeItem = new Episode { Id = Guid.NewGuid(), Path = info.Path };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, new HttpContextAccessor(), tmdbApi, storeStub.Object);

            _ = await provider.GetMetadata(info, CancellationToken.None);

            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenEpisodeItemIsMissing()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object);

            _ = await provider.GetMetadata(info, CancellationToken.None);

            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenEpisodeItemIdIsEmpty()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var episodeItem = new Episode { Id = Guid.Empty, Path = info.Path };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object);

            _ = await provider.GetMetadata(info, CancellationToken.None);

            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenRefreshModeIsNotSearchMissingMetadata()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var episodeItem = new Episode { Id = Guid.NewGuid(), Path = info.Path };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "true"),
            };

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object);

            _ = await provider.GetMetadata(info, CancellationToken.None);

            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
        }

        private static EpisodeProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IEpisodeTitleBackfillCandidateStore store)
        {
            var constructor = typeof(EpisodeProvider).GetConstructor(new[]
            {
                typeof(IHttpClientFactory),
                typeof(ILoggerFactory),
                typeof(ILibraryManager),
                typeof(IHttpContextAccessor),
                typeof(DoubanApi),
                typeof(TmdbApi),
                typeof(OmdbApi),
                typeof(ImdbApi),
                typeof(TvdbApi),
                typeof(IEpisodeTitleBackfillCandidateStore),
            });

            Assert.IsNotNull(constructor, "EpisodeProvider 尚未注入 IEpisodeTitleBackfillCandidateStore");

            var loggerFactory = LoggerFactory.Create(builder => { });
            return (EpisodeProvider)constructor!.Invoke(new object[]
            {
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager,
                httpContextAccessor,
                new DoubanApi(loggerFactory),
                tmdbApi,
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new TvdbApi(loggerFactory),
                store,
            });
        }

        private static DefaultHttpContext CreateHttpContext(string metadataRefreshMode, string replaceAllMetadata)
        {
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString($"?metadataRefreshMode={metadataRefreshMode}&replaceAllMetadata={replaceAllMetadata}");
            return context;
        }

        private static EpisodeInfo CreateEpisodeInfo()
        {
            return new EpisodeInfo
            {
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
                MetadataLanguage = "zh-CN",
                ParentIndexNumber = 1,
                IndexNumber = 1,
                SeriesDisplayOrder = string.Empty,
                SeriesProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "123",
                },
            };
        }

        private static bool InvokeIsSearchMissingMetadataRefresh(string? metadataRefreshMode, string? replaceAllMetadata)
        {
            var method = typeof(EpisodeProvider).GetMethod(
                "IsSearchMissingMetadataRefresh",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.IsNotNull(method, "EpisodeProvider.IsSearchMissingMetadataRefresh 未定义");

            return (bool)method!.Invoke(null, new object?[] { metadataRefreshMode, replaceAllMetadata })!;
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
