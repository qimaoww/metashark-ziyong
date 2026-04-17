using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping
{
    [TestClass]
    [TestCategory("Stable")]
    [DoNotParallelize]
    public class ExplicitEpisodeGroupMappingProviderTest
    {
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
            ExplicitEpisodeGroupMappingTestHelper.ResetPluginConfiguration();
        }

        [TestCleanup]
        public void ResetConfigurationAfterTest()
        {
            ExplicitEpisodeGroupMappingTestHelper.ResetPluginConfiguration();
        }

        [TestMethod]
        public void ResolveEpisodeRequestAsync_ExplicitGroupMappingWinsBeforeDisplayOrderFallback()
        {
            ExplicitEpisodeGroupMappingTestHelper.ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbEpisodeGroupMap = "65942=explicit-group",
            });

            var tmdbApi = new TmdbApi(this.loggerFactory);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(
                tmdbApi,
                "explicit-group",
                "zh-CN",
                ExplicitEpisodeGroupMappingTestHelper.CreateGroup(
                    order: 1,
                    name: "映射季",
                    ExplicitEpisodeGroupMappingTestHelper.CreateEpisode(order: 0, seasonNumber: 2, episodeNumber: 5)));
            ExplicitEpisodeGroupMappingTestHelper.SeedDisplayOrderGroup(
                tmdbApi,
                65942,
                "absolute",
                "zh-CN",
                ExplicitEpisodeGroupMappingTestHelper.CreateGroup(
                    order: 1,
                    name: "绝对顺序",
                    ExplicitEpisodeGroupMappingTestHelper.CreateEpisode(order: 0, seasonNumber: 9, episodeNumber: 9)));

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateResolver(this.loggerFactory, tmdbApi);

            var resolved = Task.Run(async () =>
                    await provider.ResolveAsync(65942, 1, 1, "absolute", "zh-CN", "zh-CN", CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsNotNull(resolved, "显式 groupId 命中时，provider 不应掉回 null。 ");
            Assert.AreEqual(2, resolved.Value.SeasonNumber, "显式 groupId 应先于 displayOrder 生效。 ");
            Assert.AreEqual(5, resolved.Value.EpisodeNumber, "显式 groupId 应解析到映射后的真实剧集位置。 ");
        }

        [TestMethod]
        public void ResolveEpisodeRequestAsync_WhenMappingRemoved_FallsBackToDisplayOrder()
        {
            ExplicitEpisodeGroupMappingTestHelper.ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbEpisodeGroupMap = string.Empty,
            });

            var tmdbApi = new TmdbApi(this.loggerFactory);
            ExplicitEpisodeGroupMappingTestHelper.SeedDisplayOrderGroup(
                tmdbApi,
                65942,
                "absolute",
                "zh-CN",
                ExplicitEpisodeGroupMappingTestHelper.CreateGroup(
                    order: 1,
                    name: "绝对顺序",
                    ExplicitEpisodeGroupMappingTestHelper.CreateEpisode(order: 0, seasonNumber: 4, episodeNumber: 7)));

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateResolver(this.loggerFactory, tmdbApi);

            var resolved = Task.Run(async () =>
                    await provider.ResolveAsync(65942, 1, 1, "absolute", "zh-CN", "zh-CN", CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsNotNull(resolved, "移除映射后仍应保留 displayOrder 回退链路。 ");
            Assert.AreEqual(4, resolved.Value.SeasonNumber);
            Assert.AreEqual(7, resolved.Value.EpisodeNumber);
        }

        [TestMethod]
        public void ResolveEpisodeRequestAsync_WhenGroupIdIsInvalid_FallsBackToDisplayOrder()
        {
            ExplicitEpisodeGroupMappingTestHelper.ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbEpisodeGroupMap = "65942=invalid-group",
            });

            var tmdbApi = new TmdbApi(this.loggerFactory);
            ExplicitEpisodeGroupMappingTestHelper.SeedMissingEpisodeGroupById(tmdbApi, "invalid-group", "zh-CN");
            ExplicitEpisodeGroupMappingTestHelper.SeedDisplayOrderGroup(
                tmdbApi,
                65942,
                "absolute",
                "zh-CN",
                ExplicitEpisodeGroupMappingTestHelper.CreateGroup(
                    order: 1,
                    name: "绝对顺序",
                    ExplicitEpisodeGroupMappingTestHelper.CreateEpisode(order: 0, seasonNumber: 6, episodeNumber: 6)));

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateResolver(this.loggerFactory, tmdbApi);

            var resolved = Task.Run(async () =>
                    await provider.ResolveAsync(65942, 1, 1, "absolute", "zh-CN", "zh-CN", CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsNotNull(resolved, "无效 groupId 不应阻断 displayOrder 回退。 ");
            Assert.AreEqual(6, resolved.Value.SeasonNumber, "显式 groupId 失效后应继续回退到 displayOrder。 ");
            Assert.AreEqual(6, resolved.Value.EpisodeNumber, "显式 groupId 失效后应继续解析 displayOrder 对应剧集。 ");
        }

        [TestMethod]
        public void ResolveEpisodeRequestAsync_WhenGroupMappingIsInvalid_FallsBackToDefaultEpisodeRequest()
        {
            ExplicitEpisodeGroupMappingTestHelper.ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbEpisodeGroupMap = "65942=invalid-group",
            });

            var tmdbApi = new TmdbApi(this.loggerFactory);
            ExplicitEpisodeGroupMappingTestHelper.SeedMissingEpisodeGroupById(tmdbApi, "invalid-group", "zh-CN");

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateResolver(this.loggerFactory, tmdbApi);

            var resolved = Task.Run(async () =>
                    await provider.ResolveAsync(65942, 1, 3, string.Empty, "zh-CN", "zh-CN", CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsNotNull(resolved, "无效 groupId 不应导致解析链崩溃。 ");
            Assert.AreEqual(1, resolved.Value.SeasonNumber, "显式映射失效时应回退到默认 season/episode 请求。 ");
            Assert.AreEqual(3, resolved.Value.EpisodeNumber, "显式映射失效时应保留原始 episode 编号。 ");
        }
    }

    internal static class ExplicitEpisodeGroupMappingTestHelper
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-explicit-episode-group-mapping-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        public static void ResetPluginConfiguration()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        public static void EnsurePluginInstance()
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

        public static void ReplacePluginConfiguration(PluginConfiguration configuration)
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

        public static TvGroup CreateGroup(int order, string name, params TvGroupEpisode[] episodes)
        {
            return new TvGroup
            {
                Order = order,
                Name = name,
                Episodes = episodes.ToList(),
            };
        }

        public static TvGroupEpisode CreateEpisode(int order, int seasonNumber, int episodeNumber)
        {
            return new TvGroupEpisode
            {
                Order = order,
                SeasonNumber = seasonNumber,
                EpisodeNumber = episodeNumber,
            };
        }

        public static void SeedEpisodeGroupById(TmdbApi tmdbApi, string groupId, string language, params TvGroup[] groups)
        {
            var collection = new TvGroupCollection
            {
                Id = groupId,
                Name = groupId,
                Groups = groups.ToList(),
                GroupCount = groups.Length,
                EpisodeCount = groups.Sum(group => group.Episodes?.Count ?? 0),
            };

            GetTmdbMemoryCache(tmdbApi).Set($"group-id-{groupId}-{language}", collection, TimeSpan.FromMinutes(5));
        }

        public static void SeedMissingEpisodeGroupById(TmdbApi tmdbApi, string groupId, string language)
        {
            GetTmdbMemoryCache(tmdbApi).Set($"group-id-{groupId}-{language}", (TvGroupCollection?)null, TimeSpan.FromMinutes(5));
        }

        public static void SeedDisplayOrderGroup(TmdbApi tmdbApi, int seriesTmdbId, string displayOrder, string language, params TvGroup[] groups)
        {
            var collection = new TvGroupCollection
            {
                Id = $"{seriesTmdbId}-{displayOrder}",
                Name = displayOrder,
                Groups = groups.ToList(),
                GroupCount = groups.Length,
                EpisodeCount = groups.Sum(group => group.Episodes?.Count ?? 0),
            };

            GetTmdbMemoryCache(tmdbApi).Set($"group-{seriesTmdbId}-{displayOrder}-{language}", collection, TimeSpan.FromMinutes(5));
        }

        public static void SeedSeason(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, string language, string seasonName, string overview)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"season-{seriesTmdbId}-s{seasonNumber}-{language}-{language}",
                new TvSeason
                {
                    Name = seasonName,
                    Overview = overview,
                    AirDate = new DateTime(2020, 1, 15),
                },
                TimeSpan.FromMinutes(5));
        }

        public static EpisodeRequestResolverHarness CreateResolver(ILoggerFactory loggerFactory, TmdbApi tmdbApi)
        {
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);
            return new EpisodeRequestResolverHarness(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
        }

        public static SeasonProvider CreateSeasonProvider(ILoggerFactory loggerFactory, TmdbApi tmdbApi)
        {
            var httpClientFactory = new DefaultHttpClientFactory();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(loggerFactory);
            var omdbApi = new OmdbApi(loggerFactory);
            var imdbApi = new ImdbApi(loggerFactory);
            return new SeasonProvider(httpClientFactory, loggerFactory, libraryManagerStub.Object, httpContextAccessorStub.Object, doubanApi, tmdbApi, omdbApi, imdbApi);
        }

        private static void EnsurePluginConfiguration()
        {
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);

            if (plugin!.Configuration != null)
            {
                return;
            }

            ReplacePluginConfiguration(new PluginConfiguration());
        }

        private static IMemoryCache GetTmdbMemoryCache(TmdbApi tmdbApi)
        {
            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "TmdbApi.memoryCache 未定义");

            var memoryCache = memoryCacheField!.GetValue(tmdbApi) as IMemoryCache;
            Assert.IsNotNull(memoryCache, "TmdbApi.memoryCache 不是有效的 IMemoryCache");
            return memoryCache!;
        }

        internal sealed class EpisodeRequestResolverHarness : BaseProvider
        {
            public EpisodeRequestResolverHarness(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi)
                : base(httpClientFactory, loggerFactory.CreateLogger<EpisodeRequestResolverHarness>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
            {
            }

            public Task<(int SeasonNumber, int EpisodeNumber)?> ResolveAsync(int seriesTmdbId, int? seasonNumber, int? episodeNumber, string displayOrder, string? language, string? imageLanguages, CancellationToken cancellationToken)
            {
                return this.ResolveEpisodeRequestAsync(seriesTmdbId, seasonNumber, episodeNumber, displayOrder, language, imageLanguages, cancellationToken);
            }
        }
    }
}
