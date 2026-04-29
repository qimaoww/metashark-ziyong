using System;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class DefaultScraperPolicyTest
    {
        [TestMethod]
        public void DeclaresAllSupportedSemanticsExplicitly()
        {
            CollectionAssert.AreEquivalent(
                new[] { "ManualSearch", "ManualMatch", "UserRefresh", "AutomaticRefresh", "OverwriteRefresh" },
                Enum.GetNames(typeof(DefaultScraperSemantic)));
        }

        [DataTestMethod]
        [DataRow(DefaultScraperSemantic.ManualSearch)]
        [DataRow(DefaultScraperSemantic.ManualMatch)]
        [DataRow(DefaultScraperSemantic.UserRefresh)]
        [DataRow(DefaultScraperSemantic.AutomaticRefresh)]
        [DataRow(DefaultScraperSemantic.OverwriteRefresh)]
        public void DefaultModeAllowsDoubanForAllSemantics(DefaultScraperSemantic semantic)
        {
            var configuration = new PluginConfiguration();

            var result = DefaultScraperPolicy.IsDoubanAllowed(configuration, semantic);

            Assert.IsTrue(result, $"default 模式下应允许 {semantic} 使用 Douban。");
        }

        [DataTestMethod]
        [DataRow(DefaultScraperSemantic.UserRefresh)]
        [DataRow(DefaultScraperSemantic.AutomaticRefresh)]
        [DataRow(DefaultScraperSemantic.OverwriteRefresh)]
        public void TmdbOnlyBlocksDoubanForNonManualSemantics(DefaultScraperSemantic semantic)
        {
            var configuration = CreateConfiguration(PluginConfiguration.DefaultScraperModeTmdbOnly);

            var result = DefaultScraperPolicy.IsDoubanAllowed(configuration, semantic);

            Assert.IsFalse(result, $"tmdb-only 模式下应禁止 {semantic} 访问 Douban。");
        }

        [DataTestMethod]
        [DataRow(DefaultScraperSemantic.ManualSearch)]
        [DataRow(DefaultScraperSemantic.ManualMatch)]
        public void TmdbOnlyAllowsDoubanForManualSemantics(DefaultScraperSemantic semantic)
        {
            var configuration = CreateConfiguration(PluginConfiguration.DefaultScraperModeTmdbOnly);

            var result = DefaultScraperPolicy.IsDoubanAllowed(configuration, semantic);

            Assert.IsTrue(result, $"tmdb-only 模式下应允许 {semantic} 使用 Douban。");
        }

        [TestMethod]
        public void NullConfigurationFallsBackToDefaultBehavior()
        {
            var automaticResult = DefaultScraperPolicy.IsDoubanAllowed(null, DefaultScraperSemantic.AutomaticRefresh);
            var manualResult = DefaultScraperPolicy.IsDoubanAllowed(null, DefaultScraperSemantic.ManualMatch);

            Assert.IsTrue(automaticResult, "配置对象为空时必须回退到 default，而不是泄漏进策略层。");
            Assert.IsTrue(manualResult, "配置对象为空时必须回退到 default，而不是改变手动路径语义。");
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("invalid-mode")]
        public void InvalidOrMissingConfigFallsBackToDefaultBehavior(string? configuredMode)
        {
            var configuration = CreateConfiguration(configuredMode);

            var automaticResult = DefaultScraperPolicy.IsDoubanAllowed(configuration, DefaultScraperSemantic.AutomaticRefresh);
            var manualResult = DefaultScraperPolicy.IsDoubanAllowed(configuration, DefaultScraperSemantic.ManualMatch);

            Assert.IsTrue(automaticResult, "缺失或非法配置值必须回退到 default，而不是泄漏进策略层。");
            Assert.IsTrue(manualResult, "缺失或非法配置值必须回退到 default，而不是改变手动路径语义。");
        }

        [TestMethod]
        public void TmdbFlagsRemainIndependentFromDoubanPolicy()
        {
            var configuration = CreateConfiguration(PluginConfiguration.DefaultScraperModeTmdbOnly);
            configuration.EnableTmdbMatch = false;

            var automaticResult = DefaultScraperPolicy.IsDoubanAllowed(configuration, DefaultScraperSemantic.AutomaticRefresh);
            var manualResult = DefaultScraperPolicy.IsDoubanAllowed(configuration, DefaultScraperSemantic.ManualMatch);

            Assert.IsFalse(automaticResult, "tmdb-only + EnableTmdbMatch=false 时，自动链路仍不应回落到 Douban。");
            Assert.IsTrue(manualResult, "tmdb-only + EnableTmdbMatch=false 也不能影响手动匹配的 Douban。");
        }

        [TestMethod]
        public void ExposesBaseProviderPolicyHookForFutureProviderWiring()
        {
            var semanticType = typeof(DefaultScraperSemantic);
            var method = typeof(Jellyfin.Plugin.MetaShark.Providers.BaseProvider).GetMethod(
                "IsDoubanAllowed",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { semanticType },
                modifiers: null);

            Assert.IsNotNull(method, "BaseProvider 应提供受保护的 IsDoubanAllowed(DefaultScraperSemantic) 入口，供后续 provider 统一复用。");
            Assert.IsTrue(method!.IsFamily || method.IsFamilyOrAssembly, "BaseProvider.IsDoubanAllowed 应保持受保护可见性。");
            Assert.AreEqual(typeof(bool), method.ReturnType);
        }

        private static PluginConfiguration CreateConfiguration(string? mode)
        {
            return new PluginConfiguration
            {
                DefaultScraperMode = mode ?? string.Empty,
            };
        }
    }
}
