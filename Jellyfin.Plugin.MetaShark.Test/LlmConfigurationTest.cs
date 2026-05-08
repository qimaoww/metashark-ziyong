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

        [TestMethod]
        public void ShouldExposeExpectedLlmConfigurationPropertiesWithDefaults()
        {
            var configuration = new PluginConfiguration();

            AssertProperty("EnableLlmAssist", typeof(bool), false, configuration);
            AssertProperty("EnableLlmTmdbIdCorrection", typeof(bool), false, configuration);
            AssertProperty("LlmBaseUrl", typeof(string), string.Empty, configuration);
            AssertProperty("LlmApiKey", typeof(string), string.Empty, configuration);
            AssertProperty("LlmModel", typeof(string), string.Empty, configuration);
            AssertProperty("LlmTimeoutSeconds", typeof(int), 15, configuration);
            AssertProperty("LlmMaxTokens", typeof(int), 512, configuration);
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
            };

            Assert.IsTrue(configuration.EnableLlmAssist);
            Assert.IsTrue(configuration.EnableLlmTmdbIdCorrection);
            Assert.AreEqual("https://example.test/v1", configuration.LlmBaseUrl);
            Assert.AreEqual("test-key", configuration.LlmApiKey);
            Assert.AreEqual("test-model", configuration.LlmModel);
            Assert.AreEqual(12, configuration.LlmTimeoutSeconds);
            Assert.AreEqual(1024, configuration.LlmMaxTokens);
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
