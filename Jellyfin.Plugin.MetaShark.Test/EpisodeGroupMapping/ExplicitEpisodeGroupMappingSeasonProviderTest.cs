using System.Threading;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Controller.Entities.TV;
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
        public void GetMetadataByTmdb_WhenExplicitGroupMappingSeasonNameIsEnglish_PreservesExistingSeasonTitle()
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
                ExplicitEpisodeGroupMappingTestHelper.CreateGroup(order: 1, name: "Prologue", []));
            ExplicitEpisodeGroupMappingTestHelper.SeedSeason(tmdbApi, 65942, 1, "zh-CN", "TMDb 第1季", "不应覆盖显式 group 命中的季概述");

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateSeasonProvider(this.loggerFactory, tmdbApi);
            var defaultTitleInfo = new SeasonInfo
            {
                Name = "  第 1 季  ",
                IndexNumber = 1,
                MetadataLanguage = "zh-CN",
            };

            var defaultTitleResult = Task.Run(async () =>
                    await provider.GetMetadataByTmdb(defaultTitleInfo, "65942", 1, CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsTrue(defaultTitleResult.HasMetadata);
            Assert.IsNotNull(defaultTitleResult.Item);
            Assert.AreEqual("第 1 季", defaultTitleResult.Item!.Name, "显式 groupId 命中且组季名为纯英文时，应保留默认季标题并去掉首尾空白。 ");
            Assert.AreEqual(1, defaultTitleResult.Item.IndexNumber);
            Assert.IsNull(defaultTitleResult.Item.Overview, "显式 groupId 命中时仍不应掉回 GetSeasonAsync(...) 的季概述。 ");

            var customChineseTitleInfo = new SeasonInfo
            {
                Name = "  爱丽丝篇  ",
                IndexNumber = 1,
                MetadataLanguage = "zh-CN",
            };

            var customChineseTitleResult = Task.Run(async () =>
                    await provider.GetMetadataByTmdb(customChineseTitleInfo, "65942", 1, CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsTrue(customChineseTitleResult.HasMetadata);
            Assert.IsNotNull(customChineseTitleResult.Item);
            Assert.AreEqual("爱丽丝篇", customChineseTitleResult.Item!.Name, "显式 groupId 命中且组季名为纯英文时，应保留自定义中文季标题并去掉首尾空白。 ");
            Assert.AreEqual(1, customChineseTitleResult.Item.IndexNumber);
            Assert.IsNull(customChineseTitleResult.Item.Overview, "显式 groupId 命中时仍不应掉回 GetSeasonAsync(...) 的季概述。 ");
        }

        [TestMethod]
        public void GetMetadataByTmdb_WhenExistingSeasonTitleIsBlank_UsesEnglishEpisodeGroupSeasonName()
        {
            foreach (string? existingSeasonTitle in new string?[] { null, string.Empty, "   " })
            {
                var result = this.GetMetadataByTmdbWithExplicitGroupSeasonName("  Season 1  ", existingSeasonTitle);

                Assert.IsTrue(result.HasMetadata);
                Assert.IsNotNull(result.Item);
                Assert.AreEqual(
                    "Season 1",
                    result.Item!.Name,
                    $"当前季标题为 {(existingSeasonTitle is null ? "null" : $"'{existingSeasonTitle}'")} 时，不应保留空值，应改用去掉首尾空白后的英文 group 季名。 ");
                Assert.AreEqual(1, result.Item.IndexNumber);
                Assert.IsNull(result.Item.Overview, "显式 groupId 命中时仍不应掉回 GetSeasonAsync(...) 的季概述。 ");
            }

            var trimmedPreserveResult = this.GetMetadataByTmdbWithExplicitGroupSeasonName("  Season 1  ", "  第 1 季  ");

            Assert.IsTrue(trimmedPreserveResult.HasMetadata);
            Assert.IsNotNull(trimmedPreserveResult.Item);
            Assert.AreEqual("第 1 季", trimmedPreserveResult.Item!.Name, "当前季标题非空白时，英文 group 季名与当前标题都应先 Trim() 再参与决策。 ");
        }

        [TestMethod]
        public void GetMetadataByTmdb_WhenExplicitGroupMappingSeasonNameIsNonEnglish_UsesEpisodeGroupSeasonName()
        {
            var result = this.GetMetadataByTmdbWithExplicitGroupSeasonName("篇章一");

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual("篇章一", result.Item!.Name, "显式 groupId 命中且组季名为非英文时，应继续使用 episode group 的季名。 ");
            Assert.AreEqual(1, result.Item.IndexNumber);
            Assert.IsNull(result.Item.Overview, "显式 groupId 命中时仍不应掉回 GetSeasonAsync(...) 的季概述。 ");
        }

        [TestMethod]
        public void GetMetadataByTmdb_WhenExplicitGroupMappingSeasonNameIsMixedLanguage_TreatsAsNonEnglishAndUsesEpisodeGroupSeasonName()
        {
            var englishStyleControlResult = this.GetMetadataByTmdbWithExplicitGroupSeasonName("S1");
            Assert.IsTrue(englishStyleControlResult.HasMetadata);
            Assert.IsNotNull(englishStyleControlResult.Item);
            Assert.AreEqual("第 1 季", englishStyleControlResult.Item!.Name, "对照组 S1 应被视为英文样式，并保留当前季标题。 ");

            foreach (var seasonGroupName in new[] { "第 1 季 Season 1", "S1 篇章" })
            {
                var result = this.GetMetadataByTmdbWithExplicitGroupSeasonName(seasonGroupName);

                Assert.IsTrue(result.HasMetadata);
                Assert.IsNotNull(result.Item);
                Assert.AreEqual(seasonGroupName, result.Item!.Name, $"显式 groupId 命中时，混合文本季名 {seasonGroupName} 不应被误判成英文。 ");
                Assert.AreEqual(1, result.Item.IndexNumber);
                Assert.IsNull(result.Item.Overview, "显式 groupId 命中时仍不应掉回 GetSeasonAsync(...) 的季概述。 ");
            }
        }

        [TestMethod]
        public void GetMetadataByTmdb_WhenExplicitGroupMappingSeasonNameHasNoAsciiLetters_TreatsAsNonEnglishAndUsesEpisodeGroupSeasonName()
        {
            foreach (var seasonGroupName in new[] { "123", "---", "_._", "._/" })
            {
                var result = this.GetMetadataByTmdbWithExplicitGroupSeasonName(seasonGroupName);

                Assert.IsTrue(result.HasMetadata);
                Assert.IsNotNull(result.Item);
                Assert.AreEqual(seasonGroupName, result.Item!.Name, $"显式 groupId 命中时，无 ASCII 字母的季名 {seasonGroupName} 不应被误判成英文。 ");
                Assert.AreEqual(1, result.Item.IndexNumber);
                Assert.IsNull(result.Item.Overview, "显式 groupId 命中时仍不应掉回 GetSeasonAsync(...) 的季概述。 ");
            }
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
            ExplicitEpisodeGroupMappingTestHelper.SeedSeason(tmdbApi, 65942, 1, "zh-CN", "Season 1", "来自 GetSeasonAsync 的概述");

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateSeasonProvider(this.loggerFactory, tmdbApi);
            var info = new SeasonInfo
            {
                Name = "当前中文标题",
                IndexNumber = 1,
                MetadataLanguage = "zh-CN",
            };

            var result = Task.Run(async () =>
                    await provider.GetMetadataByTmdb(info, "65942", 1, CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual("Season 1", result.Item!.Name, "缺少映射时应继续直接采用 GetSeasonAsync(...) 的英文季名，而不是保留当前标题。 ");
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
            ExplicitEpisodeGroupMappingTestHelper.SeedSeason(tmdbApi, 65942, 1, "zh-CN", "Season 1", "无效映射后的季级回退");

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateSeasonProvider(this.loggerFactory, tmdbApi);
            var info = new SeasonInfo
            {
                Name = "当前中文标题",
                IndexNumber = 1,
                MetadataLanguage = "zh-CN",
            };

            var result = Task.Run(async () =>
                    await provider.GetMetadataByTmdb(info, "65942", 1, CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual("Season 1", result.Item!.Name, "无效映射时也应继续直接采用 GetSeasonAsync(...) 的英文季名，而不是保留当前标题。 ");
            Assert.AreEqual("无效映射后的季级回退", result.Item.Overview, "无效映射时也应继续走 GetSeasonAsync(...) 回退。 ");
        }

        private MetadataResult<Season> GetMetadataByTmdbWithExplicitGroupSeasonName(string seasonGroupName, string? existingSeasonTitle = "第 1 季")
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
                ExplicitEpisodeGroupMappingTestHelper.CreateGroup(order: 1, name: seasonGroupName, []));
            ExplicitEpisodeGroupMappingTestHelper.SeedSeason(tmdbApi, 65942, 1, "zh-CN", "TMDb 第1季", "不应覆盖显式 group 命中的季概述");

            var provider = ExplicitEpisodeGroupMappingTestHelper.CreateSeasonProvider(this.loggerFactory, tmdbApi);
            var info = new SeasonInfo
            {
                Name = existingSeasonTitle,
                IndexNumber = 1,
                MetadataLanguage = "zh-CN",
            };

            return Task.Run(async () =>
                    await provider.GetMetadataByTmdb(info, "65942", 1, CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();
        }
    }
}
