using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Configuration;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class DefaultScraperPolicy
    {
        private const string PolicyTypeName = "Jellyfin.Plugin.MetaShark.Providers.DefaultScraperPolicy";
        private const string SemanticTypeName = "Jellyfin.Plugin.MetaShark.Providers.DefaultScraperSemantic";

        [TestMethod]
        public void DeclaresAllSupportedSemanticsExplicitly()
        {
            var semanticType = GetSemanticType();

            Assert.IsTrue(semanticType.IsEnum, "DefaultScraperSemantic 必须是显式枚举，避免从旧 provider id 或来源字段推断语义。");

            CollectionAssert.AreEquivalent(
                new[] { "ManualSearch", "ManualMatch", "UserRefresh", "AutomaticRefresh" },
                Enum.GetNames(semanticType).ToArray());
        }

        [DataTestMethod]
        [DataRow("ManualSearch")]
        [DataRow("ManualMatch")]
        [DataRow("UserRefresh")]
        [DataRow("AutomaticRefresh")]
        public void DefaultModeAllowsDoubanForAllSemantics(string semanticName)
        {
            var configuration = new PluginConfiguration();

            var result = InvokeIsDoubanAllowed(configuration, semanticName);

            Assert.IsTrue(result, $"default 模式下应允许 {semanticName} 使用 Douban。");
        }

        [DataTestMethod]
        [DataRow("ManualSearch")]
        [DataRow("ManualMatch")]
        public void ManualPathsAllowDoubanUnderTmdbOnly(string semanticName)
        {
            var configuration = CreateConfiguration(PluginConfiguration.DefaultScraperModeTmdbOnly);

            var result = InvokeIsDoubanAllowed(configuration, semanticName);

            Assert.IsTrue(result, $"tmdb-only 模式下应保留 {semanticName} 的 Douban 手动豁免。");
        }

        [DataTestMethod]
        [DataRow("UserRefresh")]
        [DataRow("AutomaticRefresh")]
        public void AutomaticPathsBlockDoubanUnderTmdbOnly(string semanticName)
        {
            var configuration = CreateConfiguration(PluginConfiguration.DefaultScraperModeTmdbOnly);

            var result = InvokeIsDoubanAllowed(configuration, semanticName);

            Assert.IsFalse(result, $"tmdb-only 模式下应阻止 {semanticName} 访问 Douban。");
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("invalid-mode")]
        public void InvalidOrMissingConfigFallsBackToDefaultBehavior(string? configuredMode)
        {
            var configuration = new PluginConfiguration();
            configuration.DefaultScraperMode = configuredMode!;

            var automaticResult = InvokeIsDoubanAllowed(configuration, "AutomaticRefresh");
            var manualResult = InvokeIsDoubanAllowed(configuration, "ManualMatch");

            Assert.IsTrue(automaticResult, "缺失或非法配置值必须回退到 default，而不是泄漏进策略层。");
            Assert.IsTrue(manualResult, "缺失或非法配置值必须回退到 default，而不是改变手动路径语义。");
        }

        [TestMethod]
        public void TmdbFlagsRemainIndependentFromDoubanPolicy()
        {
            var configuration = CreateConfiguration(PluginConfiguration.DefaultScraperModeTmdbOnly);
            configuration.EnableTmdbMatch = false;

            var automaticResult = InvokeIsDoubanAllowed(configuration, "AutomaticRefresh");
            var manualResult = InvokeIsDoubanAllowed(configuration, "ManualMatch");

            Assert.IsFalse(automaticResult, "tmdb-only + EnableTmdbMatch=false 时，自动链路仍不应回落到 Douban。");
            Assert.IsTrue(manualResult, "tmdb-only + EnableTmdbMatch=false 不应误伤手动匹配的 Douban 显式语义。");
        }

        [TestMethod]
        public void ExposesBaseProviderPolicyHookForFutureProviderWiring()
        {
            var semanticType = GetSemanticType();
            var method = typeof(Jellyfin.Plugin.MetaShark.Providers.BaseProvider).GetMethod(
                "IsDoubanAllowed",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { semanticType },
                modifiers: null);

            Assert.IsNotNull(method, "BaseProvider 应提供受保护的 IsDoubanAllowed(DefaultScraperSemantic) 入口，供后续 provider 统一复用。");
            Assert.AreEqual(typeof(bool), method.ReturnType);
        }

        private static PluginConfiguration CreateConfiguration(string mode)
        {
            return new PluginConfiguration
            {
                DefaultScraperMode = mode,
            };
        }

        private static bool InvokeIsDoubanAllowed(PluginConfiguration? configuration, string semanticName)
        {
            var semanticType = GetSemanticType();
            var policyType = GetPolicyType();
            var method = policyType.GetMethod(
                "IsDoubanAllowed",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(PluginConfiguration), semanticType },
                modifiers: null);

            Assert.IsNotNull(method, "DefaultScraperPolicy.IsDoubanAllowed(PluginConfiguration, DefaultScraperSemantic) 未定义。");

            var semantic = Enum.Parse(semanticType, semanticName);
            var result = method.Invoke(null, new object?[] { configuration, semantic });

            Assert.IsNotNull(result, "DefaultScraperPolicy.IsDoubanAllowed 应返回布尔值结果。");
            Assert.IsInstanceOfType(result, typeof(bool));
            return (bool)result;
        }

        private static Type GetPolicyType()
        {
            var policyType = typeof(PluginConfiguration).Assembly.GetType(PolicyTypeName);
            Assert.IsNotNull(policyType, "DefaultScraperPolicy 未定义。");
            return policyType!;
        }

        private static Type GetSemanticType()
        {
            var semanticType = typeof(PluginConfiguration).Assembly.GetType(SemanticTypeName);
            Assert.IsNotNull(semanticType, "DefaultScraperSemantic 未定义。");
            return semanticType!;
        }
    }
}
