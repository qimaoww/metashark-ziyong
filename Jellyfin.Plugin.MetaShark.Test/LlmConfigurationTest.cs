using System.Reflection;
using Jellyfin.Plugin.MetaShark.Configuration;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmConfigurationTest
    {
        private const string JsonSchemaMode = "json-schema";
        private const string JsonObjectMode = "json-object";
        private const string TextJsonMode = "text-json";
        private const string DefaultReasoningEffort = "default";
        private const string NoneReasoningEffort = "none";
        private const string MinimalReasoningEffort = "minimal";
        private const string LowReasoningEffort = "low";
        private const string MediumReasoningEffort = "medium";
        private const string HighReasoningEffort = "high";
        private const string XHighReasoningEffort = "xhigh";

        [TestMethod]
        public void ShouldExposeExpectedLlmConfigurationPropertiesWithDefaults()
        {
            var configuration = new PluginConfiguration();

            AssertProperty("EnableLlmAssist", typeof(bool), false, configuration);
            AssertProperty("EnableLlmTmdbIdCorrection", typeof(bool), false, configuration);
            AssertProperty("EnableLlmTmdbCorrectionPersistence", typeof(bool), true, configuration);
            AssertProperty("EnableLlmTmdbCompletionPersistence", typeof(bool), true, configuration);
            AssertProperty("LlmTmdbCorrectionMap", typeof(string), string.Empty, configuration);
            AssertProperty("LlmTmdbCompletionMap", typeof(string), string.Empty, configuration);
            AssertProperty("LlmTmdbEpisodeGroupMap", typeof(string), string.Empty, configuration);
            AssertProperty("LlmBaseUrl", typeof(string), string.Empty, configuration);
            AssertProperty("LlmApiKey", typeof(string), string.Empty, configuration);
            AssertProperty("LlmModel", typeof(string), string.Empty, configuration);
            AssertProperty("LlmTimeoutSeconds", typeof(int), 15, configuration);
            AssertProperty("LlmMaxTokens", typeof(int), 512, configuration);
            AssertProperty("LlmReasoningEffort", typeof(string), DefaultReasoningEffort, configuration);
            AssertProperty("LlmAllowRelativePathContext", typeof(bool), true, configuration);
            AssertProperty("LlmAllowTextCompletion", typeof(bool), false, configuration);
            AssertProperty("LlmConfidenceThreshold", typeof(double), 0.75, configuration);
            AssertProperty("EnableLlmEpisodeGroupMappingAssist", typeof(bool), false, configuration);
            AssertProperty("LlmEpisodeGroupMappingMinConfidence", typeof(double), 0.80, configuration);
            AssertProperty("LlmEpisodeGroupMappingMaxCandidateGroups", typeof(int), 8, configuration);
            AssertProperty("LlmStructuredOutputMode", typeof(string), JsonSchemaMode, configuration);
        }

        [TestMethod]
        public void ShouldReadBackSupportedLlmValuesAfterSetting()
        {
            var configuration = new PluginConfiguration
            {
                EnableLlmAssist = true,
                EnableLlmTmdbIdCorrection = true,
                EnableLlmTmdbCorrectionPersistence = false,
                EnableLlmTmdbCompletionPersistence = false,
                LlmTmdbCorrectionMap = "series:douban:26862290=tmdb:65942",
                LlmTmdbCompletionMap = "series:douban:37291769=tmdb:251782",
                LlmTmdbEpisodeGroupMap = "65942=llm-group",
                LlmBaseUrl = "https://example.test/v1",
                LlmApiKey = "test-key",
                LlmModel = "test-model",
                LlmTimeoutSeconds = 12,
                LlmMaxTokens = 1024,
                LlmAllowRelativePathContext = false,
                LlmAllowTextCompletion = true,
                LlmConfidenceThreshold = 0.42,
                EnableLlmEpisodeGroupMappingAssist = true,
                LlmEpisodeGroupMappingMinConfidence = 0.88,
                LlmEpisodeGroupMappingMaxCandidateGroups = 12,
                LlmStructuredOutputMode = JsonObjectMode,
                LlmReasoningEffort = HighReasoningEffort,
            };

            Assert.IsTrue(configuration.EnableLlmAssist);
            Assert.IsTrue(configuration.EnableLlmTmdbIdCorrection);
            Assert.IsFalse(configuration.EnableLlmTmdbCorrectionPersistence);
            Assert.IsFalse(configuration.EnableLlmTmdbCompletionPersistence);
            Assert.AreEqual("series:douban:26862290=tmdb:65942", configuration.LlmTmdbCorrectionMap);
            Assert.AreEqual("series:douban:37291769=tmdb:251782", configuration.LlmTmdbCompletionMap);
            Assert.AreEqual("65942=llm-group", configuration.LlmTmdbEpisodeGroupMap);
            Assert.AreEqual("https://example.test/v1", configuration.LlmBaseUrl);
            Assert.AreEqual("test-key", configuration.LlmApiKey);
            Assert.AreEqual("test-model", configuration.LlmModel);
            Assert.AreEqual(12, configuration.LlmTimeoutSeconds);
            Assert.AreEqual(1024, configuration.LlmMaxTokens);
            Assert.AreEqual(HighReasoningEffort, configuration.LlmReasoningEffort);
            Assert.IsFalse(configuration.LlmAllowRelativePathContext);
            Assert.IsTrue(configuration.LlmAllowTextCompletion);
            Assert.AreEqual(0.42, configuration.LlmConfidenceThreshold);
            Assert.IsTrue(configuration.EnableLlmEpisodeGroupMappingAssist);
            Assert.AreEqual(0.88, configuration.LlmEpisodeGroupMappingMinConfidence);
            Assert.AreEqual(12, configuration.LlmEpisodeGroupMappingMaxCandidateGroups);
            Assert.AreEqual(JsonObjectMode, configuration.LlmStructuredOutputMode);

            configuration.LlmStructuredOutputMode = TextJsonMode;

            Assert.AreEqual(TextJsonMode, configuration.LlmStructuredOutputMode);
        }

        [TestMethod]
        public void ShouldNormalizeOutOfRangeNumbersToNearestAllowedValue()
        {
            var configuration = new PluginConfiguration
            {
                LlmTimeoutSeconds = 0,
                LlmMaxTokens = 63,
                LlmConfidenceThreshold = -0.1,
                LlmEpisodeGroupMappingMinConfidence = -0.1,
                LlmEpisodeGroupMappingMaxCandidateGroups = 0,
            };

            Assert.AreEqual(1, configuration.LlmTimeoutSeconds, "LlmTimeoutSeconds 下限必须保持 1 秒。");
            Assert.AreEqual(64, configuration.LlmMaxTokens);
            Assert.AreEqual(0.0, configuration.LlmConfidenceThreshold);
            Assert.AreEqual(0.0, configuration.LlmEpisodeGroupMappingMinConfidence);
            Assert.AreEqual(1, configuration.LlmEpisodeGroupMappingMaxCandidateGroups);

            configuration.LlmTimeoutSeconds = 31;
            configuration.LlmMaxTokens = 4097;
            configuration.LlmConfidenceThreshold = 1.1;
            configuration.LlmEpisodeGroupMappingMinConfidence = 1.1;
            configuration.LlmEpisodeGroupMappingMaxCandidateGroups = 51;

            Assert.AreEqual(30, configuration.LlmTimeoutSeconds, "LlmTimeoutSeconds 上限必须保持 30 秒。");
            Assert.AreEqual(4096, configuration.LlmMaxTokens);
            Assert.AreEqual(1.0, configuration.LlmConfidenceThreshold);
            Assert.AreEqual(1.0, configuration.LlmEpisodeGroupMappingMinConfidence);
            Assert.AreEqual(50, configuration.LlmEpisodeGroupMappingMaxCandidateGroups);
        }

        [TestMethod]
        public void ShouldNormalizeNonFiniteDoubleValuesToDefaults()
        {
            var configuration = new PluginConfiguration
            {
                LlmConfidenceThreshold = double.NaN,
                LlmEpisodeGroupMappingMinConfidence = double.PositiveInfinity,
            };

            Assert.AreEqual(0.75, configuration.LlmConfidenceThreshold, "普通 LLM 置信度阈值不得保留 NaN。");
            Assert.AreEqual(0.80, configuration.LlmEpisodeGroupMappingMinConfidence, "剧集组映射置信度阈值不得保留非有限值。");

            configuration.LlmConfidenceThreshold = double.NegativeInfinity;
            configuration.LlmEpisodeGroupMappingMinConfidence = double.NaN;

            Assert.AreEqual(0.75, configuration.LlmConfidenceThreshold, "普通 LLM 置信度阈值不得保留非有限值。");
            Assert.AreEqual(0.80, configuration.LlmEpisodeGroupMappingMinConfidence, "剧集组映射置信度阈值不得保留 NaN。");
        }

        [TestMethod]
        public void ShouldNormalizeInvalidStructuredOutputModeToJsonSchema()
        {
            var configuration = new PluginConfiguration
            {
                LlmStructuredOutputMode = JsonObjectMode,
            };

            Assert.AreEqual(JsonObjectMode, configuration.LlmStructuredOutputMode);

            configuration.LlmStructuredOutputMode = string.Empty;

            Assert.AreEqual(JsonSchemaMode, configuration.LlmStructuredOutputMode);

            configuration.LlmStructuredOutputMode = "markdown";

            Assert.AreEqual(JsonSchemaMode, configuration.LlmStructuredOutputMode);
        }

        [TestMethod]
        public void ShouldNormalizeInvalidReasoningEffortToDefault()
        {
            var configuration = new PluginConfiguration();
            configuration.LlmReasoningEffort = LowReasoningEffort;

            Assert.AreEqual(LowReasoningEffort, configuration.LlmReasoningEffort);

            configuration.LlmReasoningEffort = string.Empty;

            Assert.AreEqual(DefaultReasoningEffort, configuration.LlmReasoningEffort);

            configuration.LlmReasoningEffort = "super-high";

            Assert.AreEqual(DefaultReasoningEffort, configuration.LlmReasoningEffort);
        }

        [TestMethod]
        public void ShouldAllowSupportedReasoningEffortValues()
        {
            var configuration = new PluginConfiguration();

            foreach (var value in new[] { DefaultReasoningEffort, NoneReasoningEffort, MinimalReasoningEffort, LowReasoningEffort, MediumReasoningEffort, HighReasoningEffort, XHighReasoningEffort })
            {
                configuration.LlmReasoningEffort = value;

                Assert.AreEqual(value, configuration.LlmReasoningEffort, $"{value} 必须是可持久化的 reasoning_effort 值。");
            }
        }

        [TestMethod]
        public void ShouldKeepEmptyStringsEmptyAfterSetting()
        {
            var configuration = new PluginConfiguration
            {
                LlmBaseUrl = "https://example.test/v1",
                LlmApiKey = "test-key",
                LlmModel = "test-model",
            };

            configuration.LlmBaseUrl = string.Empty;
            configuration.LlmApiKey = string.Empty;
            configuration.LlmModel = string.Empty;

            Assert.AreEqual(string.Empty, configuration.LlmBaseUrl);
            Assert.AreEqual(string.Empty, configuration.LlmApiKey);
            Assert.AreEqual(string.Empty, configuration.LlmModel);
        }

        [TestMethod]
        public void ShouldNotExposeSeparateExternalIdResolutionSwitch()
        {
            var property = typeof(PluginConfiguration).GetProperty("EnableLlmExternalIdResolution", BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNull(property, "外部 ID 辅助解析复用 EnableLlmAssist，不应暴露未实现的独立开关。 ");
        }

        [TestMethod]
        public void ShouldAllowTogglingTmdbCorrectionSwitchWithoutChangingDefault()
        {
            var configuration = new PluginConfiguration();

            Assert.IsFalse(configuration.EnableLlmTmdbIdCorrection, "TMDb 纠错开关默认必须关闭。");

            configuration.EnableLlmTmdbIdCorrection = true;
            Assert.IsTrue(configuration.EnableLlmTmdbIdCorrection, "TMDb 纠错开关应能保存 true。");

            configuration.EnableLlmTmdbIdCorrection = false;
            Assert.IsFalse(configuration.EnableLlmTmdbIdCorrection, "TMDb 纠错开关应能读回 false。");
        }

        private static void AssertProperty(string propertyName, Type expectedType, object expectedDefault, PluginConfiguration configuration)
        {
            var property = typeof(PluginConfiguration).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(property, $"PluginConfiguration.{propertyName} 未定义");
            Assert.AreEqual(expectedType, property.PropertyType, $"PluginConfiguration.{propertyName} 类型不正确");
            Assert.AreEqual(expectedDefault, property.GetValue(configuration), $"PluginConfiguration.{propertyName} 默认值不正确");
        }
    }
}
