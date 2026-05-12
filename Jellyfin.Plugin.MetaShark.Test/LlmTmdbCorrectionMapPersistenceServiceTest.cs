using System.Reflection;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    [TestCategory("Stable")]
    public class LlmTmdbCorrectionMapPersistenceServiceTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-llm-tmdb-correction-map-persistence-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");
        private static RecordingXmlSerializer? currentSerializer;

        [TestInitialize]
        public void ResetBeforeEachTest()
        {
            if (Directory.Exists(PluginTestRootPath))
            {
                Directory.Delete(PluginTestRootPath, recursive: true);
            }

            currentSerializer = new RecordingXmlSerializer();
            RecreatePluginInstance(currentSerializer);
            ReplacePluginConfiguration(new PluginConfiguration());
            DeleteConfigurationFileIfExists();
        }

        [TestMethod]
        public async Task TryUpsertDoubanCorrectionAsync_WhenEnabled_ShouldPersistCanonicalMappingAndUpdateConfiguration()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableLlmTmdbCorrectionPersistence = true,
                LlmTmdbCorrectionMap = "series:douban:111=tmdb:222",
            });
            var service = new LlmTmdbCorrectionMapPersistenceService();
            Assert.IsNotNull(currentSerializer);
            var saveCallCountBefore = currentSerializer.SerializeToFileCallCount;

            var result = await service.TryUpsertDoubanCorrectionAsync("Series", "26862290", "65942", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmTmdbCorrectionMapPersistenceStatus.Saved, result.Status);
            Assert.AreEqual("series:douban:111=tmdb:222", result.PreviousMapping);
            Assert.AreEqual("series:douban:111=tmdb:222\nseries:douban:26862290=tmdb:65942", result.CurrentMapping);
            Assert.AreEqual("series:douban:111=tmdb:222\nseries:douban:26862290=tmdb:65942", MetaSharkPlugin.Instance!.Configuration.LlmTmdbCorrectionMap);
            Assert.AreEqual(saveCallCountBefore + 1, currentSerializer.SerializeToFileCallCount);
            Assert.AreEqual(MetaSharkPlugin.Instance.ConfigurationFilePath, currentSerializer.LastSerializedFilePath);
            var fileContent = File.ReadAllText(MetaSharkPlugin.Instance.ConfigurationFilePath);
            StringAssert.Contains(fileContent, "series:douban:26862290=tmdb:65942");
        }

        [TestMethod]
        public async Task TryUpsertDoubanCompletionAsync_WhenEnabled_ShouldPersistCanonicalMappingAndUpdateConfiguration()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableLlmTmdbCompletionPersistence = true,
                LlmTmdbCompletionMap = "movie:douban:111=tmdb:222",
            });
            var service = new LlmTmdbCorrectionMapPersistenceService();
            Assert.IsNotNull(currentSerializer);
            var saveCallCountBefore = currentSerializer.SerializeToFileCallCount;

            var result = await service.TryUpsertDoubanCompletionAsync("Series", "37291769", "251782", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmTmdbCorrectionMapPersistenceStatus.Saved, result.Status);
            Assert.AreEqual("movie:douban:111=tmdb:222", result.PreviousMapping);
            Assert.AreEqual("movie:douban:111=tmdb:222\nseries:douban:37291769=tmdb:251782", result.CurrentMapping);
            Assert.AreEqual("movie:douban:111=tmdb:222\nseries:douban:37291769=tmdb:251782", MetaSharkPlugin.Instance!.Configuration.LlmTmdbCompletionMap);
            Assert.AreEqual(string.Empty, MetaSharkPlugin.Instance.Configuration.LlmTmdbCorrectionMap);
            Assert.AreEqual(saveCallCountBefore + 1, currentSerializer.SerializeToFileCallCount);
            Assert.AreEqual(MetaSharkPlugin.Instance.ConfigurationFilePath, currentSerializer.LastSerializedFilePath);
            var fileContent = File.ReadAllText(MetaSharkPlugin.Instance.ConfigurationFilePath);
            StringAssert.Contains(fileContent, "series:douban:37291769=tmdb:251782");
            Assert.IsFalse(fileContent.Contains("<LlmTmdbCorrectionMap>series:douban:37291769=tmdb:251782</LlmTmdbCorrectionMap>", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task TryUpsertDoubanCompletionAsync_WhenDisabled_ShouldNotSave()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableLlmTmdbCompletionPersistence = false,
                LlmTmdbCompletionMap = string.Empty,
            });
            var service = new LlmTmdbCorrectionMapPersistenceService();

            var result = await service.TryUpsertDoubanCompletionAsync("Series", "37291769", "251782", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmTmdbCorrectionMapPersistenceStatus.Failed, result.Status);
            Assert.AreEqual("LlmTmdbCompletionPersistenceDisabled", result.Reason);
            Assert.AreEqual(string.Empty, MetaSharkPlugin.Instance!.Configuration.LlmTmdbCompletionMap);
            Assert.IsFalse(File.Exists(MetaSharkPlugin.Instance.ConfigurationFilePath));
        }

        [TestMethod]
        public async Task TryUpsertDoubanCorrectionAsync_WhenDisabled_ShouldNotSave()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableLlmTmdbCorrectionPersistence = false,
                LlmTmdbCorrectionMap = string.Empty,
            });
            var service = new LlmTmdbCorrectionMapPersistenceService();

            var result = await service.TryUpsertDoubanCorrectionAsync("Series", "26862290", "65942", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmTmdbCorrectionMapPersistenceStatus.Failed, result.Status);
            Assert.AreEqual("LlmTmdbCorrectionPersistenceDisabled", result.Reason);
            Assert.AreEqual(string.Empty, MetaSharkPlugin.Instance!.Configuration.LlmTmdbCorrectionMap);
            Assert.IsFalse(File.Exists(MetaSharkPlugin.Instance.ConfigurationFilePath));
        }

        private static void DeleteConfigurationFileIfExists()
        {
            var plugin = MetaSharkPlugin.Instance;
            if (plugin != null && File.Exists(plugin.ConfigurationFilePath))
            {
                File.Delete(plugin.ConfigurationFilePath);
            }
        }

        private static void RecreatePluginInstance(IXmlSerializer xmlSerializer)
        {
            Directory.CreateDirectory(PluginsPath);
            Directory.CreateDirectory(PluginConfigurationsPath);

            var appHost = new Moq.Mock<IServerApplicationHost>();
            appHost.Setup(x => x.GetLocalApiUrl(Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<int?>())).Returns("http://127.0.0.1:8096");
            var applicationPaths = new Moq.Mock<IApplicationPaths>();
            applicationPaths.SetupGet(x => x.PluginsPath).Returns(PluginsPath);
            applicationPaths.SetupGet(x => x.PluginConfigurationsPath).Returns(PluginConfigurationsPath);

            _ = new MetaSharkPlugin(appHost.Object, applicationPaths.Object, xmlSerializer);
        }

        private static void ReplacePluginConfiguration(PluginConfiguration configuration)
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

            Assert.Fail("Could not initialize MetaSharkPlugin configuration for tests.");
        }

        private class RecordingXmlSerializer : IXmlSerializer
        {
            public int SerializeToFileCallCount { get; private set; }

            public string LastSerializedFilePath { get; private set; } = string.Empty;

            public object DeserializeFromStream(Type type, Stream stream)
            {
                throw new NotSupportedException();
            }

            public void SerializeToStream(object obj, Stream stream)
            {
                throw new NotSupportedException();
            }

            public virtual void SerializeToFile(object obj, string file)
            {
                this.SerializeToFileCallCount++;
                this.LastSerializedFilePath = file;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var correctionMapping = obj is PluginConfiguration configuration
                    ? configuration.LlmTmdbCorrectionMap
                    : string.Empty;
                var completionMapping = obj is PluginConfiguration completionConfiguration
                    ? completionConfiguration.LlmTmdbCompletionMap
                    : string.Empty;
                File.WriteAllText(file, $"<PluginConfiguration><LlmTmdbCorrectionMap>{correctionMapping}</LlmTmdbCorrectionMap><LlmTmdbCompletionMap>{completionMapping}</LlmTmdbCompletionMap></PluginConfiguration>");
            }

            public object DeserializeFromFile(Type type, string file)
            {
                throw new NotSupportedException();
            }

            public object DeserializeFromBytes(Type type, byte[] buffer)
            {
                throw new NotSupportedException();
            }
        }
    }
}
