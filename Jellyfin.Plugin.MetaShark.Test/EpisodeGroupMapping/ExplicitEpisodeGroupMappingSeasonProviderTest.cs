using System.Threading;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping
{
    [TestClass]
    [TestCategory("Stable")]
    [DoNotParallelize]
    public class ExplicitEpisodeGroupMappingSeasonProviderTest
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
        public void GetMetadataByTmdb_WhenExplicitGroupMappingHits_UsesEpisodeGroupSeasonName()
        {
            ExplicitEpisodeGroupMappingTestHelper.ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbEpisodeGroupMap = "65942=season-group",
            });

            var tmdbApi = new TmdbApi(this.loggerFactory);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(
                tmdbApi,
                "season-group",
                "zh-CN",
                ExplicitEpisodeGroupMappingTestHelper.CreateGroup(order: 1, name: "篇章一", []));
            ExplicitEpisodeGroupMappingTestHelper.SeedSeason(tmdbApi, 65942, 1, "zh-CN", "TMDb 第1季", "不应覆盖显式 group 命中的季概述");

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateSeasonProvider(this.loggerFactory, tmdbApi);
            var info = new SeasonInfo
            {
                Name = "第 1 季",
                IndexNumber = 1,
                MetadataLanguage = "zh-CN",
            };

            var result = Task.Run(async () =>
                    await provider.GetMetadataByTmdb(info, "65942", 1, CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual("篇章一", result.Item!.Name, "显式 groupId 命中时，季名应直接来自 episode group。 ");
            Assert.AreEqual(1, result.Item.IndexNumber);
            Assert.IsNull(result.Item.Overview, "显式 groupId 命中时不应掉回 GetSeasonAsync(...) 的季概述。 ");
            Assert.IsNull(result.Item.PremiereDate, "显式 groupId 命中时不应携带 GetSeasonAsync(...) 的首播日期。 ");
            Assert.IsNull(result.Item.ProductionYear, "显式 groupId 命中时不应携带 GetSeasonAsync(...) 的年份。 ");
            Assert.IsFalse(result.Item.ProviderIds?.ContainsKey(MetadataProvider.Tvdb.ToString()) ?? false, "显式 groupId 命中时不应带入 GetSeasonAsync(...) 的 TVDB id。 ");
        }

        [TestMethod]
        public void GetMetadataByTmdb_WhenMappingMissing_FallsBackToGetSeasonAsync()
        {
            ExplicitEpisodeGroupMappingTestHelper.ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbEpisodeGroupMap = string.Empty,
            });

            var tmdbApi = new TmdbApi(this.loggerFactory);
            ExplicitEpisodeGroupMappingTestHelper.SeedSeason(tmdbApi, 65942, 1, "zh-CN", "TMDb 第1季", "来自 GetSeasonAsync 的概述");

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateSeasonProvider(this.loggerFactory, tmdbApi);
            var info = new SeasonInfo
            {
                Name = "第 1 季",
                IndexNumber = 1,
                MetadataLanguage = "zh-CN",
            };

            var result = Task.Run(async () =>
                    await provider.GetMetadataByTmdb(info, "65942", 1, CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual("TMDb 第1季", result.Item!.Name);
            Assert.AreEqual("来自 GetSeasonAsync 的概述", result.Item.Overview, "缺少映射时应回退到 GetSeasonAsync(...)。 ");
        }

        [TestMethod]
        public void GetMetadataByTmdb_WhenMappingIsInvalid_FallsBackToGetSeasonAsync()
        {
            ExplicitEpisodeGroupMappingTestHelper.ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbEpisodeGroupMap = "65942=broken-group",
            });

            var tmdbApi = new TmdbApi(this.loggerFactory);
            ExplicitEpisodeGroupMappingTestHelper.SeedMissingEpisodeGroupById(tmdbApi, "broken-group", "zh-CN");
            ExplicitEpisodeGroupMappingTestHelper.SeedSeason(tmdbApi, 65942, 1, "zh-CN", "TMDb 回退季名", "无效映射后的季级回退");

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateSeasonProvider(this.loggerFactory, tmdbApi);
            var info = new SeasonInfo
            {
                Name = "第 1 季",
                IndexNumber = 1,
                MetadataLanguage = "zh-CN",
            };

            var result = Task.Run(async () =>
                    await provider.GetMetadataByTmdb(info, "65942", 1, CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual("TMDb 回退季名", result.Item!.Name);
            Assert.AreEqual("无效映射后的季级回退", result.Item.Overview, "无效映射时也应继续走 GetSeasonAsync(...) 回退。 ");
        }
    }
}
