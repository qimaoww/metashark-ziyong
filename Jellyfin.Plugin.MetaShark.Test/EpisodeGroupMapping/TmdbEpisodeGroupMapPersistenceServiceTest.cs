using System.Reflection;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping
{
    [TestClass]
    [DoNotParallelize]
    [TestCategory("Stable")]
    public class TmdbEpisodeGroupMapPersistenceServiceTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-group-persistence-tests");
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
        public async Task TrySaveAsync_WhenMappingChanged_ShouldPersistCanonicalMappingAndUpdateConfiguration()
        {
            ReplacePluginConfiguration(CreateConfiguration(existingMapping: "70000=group-b"));
            var service = new TmdbEpisodeGroupMapPersistenceService();
            Assert.IsNotNull(currentSerializer);
            var saveCallCountBefore = currentSerializer.SerializeToFileCallCount;

            var result = await service.TrySaveAsync("70000=group-b", "70000=group-b\n65942 = candidate-group", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(TmdbEpisodeGroupMapPersistenceStatus.Saved, result.Status);
            Assert.AreEqual("70000=group-b", result.PreviousMapping);
            Assert.AreEqual("65942=candidate-group\n70000=group-b", result.CurrentMapping);
            Assert.AreEqual("65942=candidate-group\n70000=group-b", MetaSharkPlugin.Instance!.Configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual(saveCallCountBefore + 1, currentSerializer.SerializeToFileCallCount);
            Assert.AreEqual(MetaSharkPlugin.Instance.ConfigurationFilePath, currentSerializer.LastSerializedFilePath);
            var fileContent = File.ReadAllText(MetaSharkPlugin.Instance.ConfigurationFilePath);
            StringAssert.Contains(fileContent, "65942=candidate-group");
            StringAssert.Contains(fileContent, "70000=group-b");
        }

        [TestMethod]
        public async Task TrySaveAsync_WhenExpectedAndNewMappingsAreSemanticallySame_ShouldReturnNoChangeWithoutSaving()
        {
            ReplacePluginConfiguration(CreateConfiguration(existingMapping: "65942=group-a\n70000=group-b"));
            var service = new TmdbEpisodeGroupMapPersistenceService();

            var result = await service.TrySaveAsync("70000 = group-b\n65942 = group-a", "# comment\n65942=group-a\n70000=group-b", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(TmdbEpisodeGroupMapPersistenceStatus.NoChange, result.Status);
            Assert.AreEqual("65942=group-a\n70000=group-b", result.CurrentMapping);
            Assert.AreEqual("65942=group-a\n70000=group-b", MetaSharkPlugin.Instance!.Configuration.TmdbEpisodeGroupMap);
            Assert.IsFalse(File.Exists(MetaSharkPlugin.Instance.ConfigurationFilePath));
        }

        [TestMethod]
        public async Task TrySaveAsync_WhenCurrentMappingChangedBeforeSave_ShouldReturnConflictAndNotOverwrite()
        {
            ReplacePluginConfiguration(CreateConfiguration(existingMapping: "65942=other-group"));
            var service = new TmdbEpisodeGroupMapPersistenceService();

            var result = await service.TrySaveAsync("65942=old-group", "65942=new-group", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(TmdbEpisodeGroupMapPersistenceStatus.Conflict, result.Status);
            Assert.AreEqual("65942=old-group", result.PreviousMapping);
            Assert.AreEqual("65942=other-group", result.CurrentMapping);
            Assert.AreEqual("65942=other-group", MetaSharkPlugin.Instance!.Configuration.TmdbEpisodeGroupMap);
            Assert.IsFalse(File.Exists(MetaSharkPlugin.Instance.ConfigurationFilePath));
        }

        [TestMethod]
        public async Task TrySaveAsync_WhenSaveThrows_ShouldReturnFailedAndRollbackConfigurationAndFile()
        {
            currentSerializer = new ThrowingXmlSerializer(new InvalidOperationException("boom"));
            RecreatePluginInstance(currentSerializer);
            ReplacePluginConfiguration(CreateConfiguration(existingMapping: "65942=old-group"));
            var originalFilePath = MetaSharkPlugin.Instance!.ConfigurationFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(originalFilePath)!);
            File.WriteAllText(originalFilePath, "<PluginConfiguration><TmdbEpisodeGroupMap>65942=old-group</TmdbEpisodeGroupMap></PluginConfiguration>");

            var service = new TmdbEpisodeGroupMapPersistenceService();

            var result = await service.TrySaveAsync("65942=old-group", "65942=new-group", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(TmdbEpisodeGroupMapPersistenceStatus.Failed, result.Status);
            Assert.AreEqual("SaveConfigurationFailed", result.Reason);
            Assert.IsNotNull(result.Exception);
            Assert.AreEqual("65942=old-group", MetaSharkPlugin.Instance.Configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual("<PluginConfiguration><TmdbEpisodeGroupMap>65942=old-group</TmdbEpisodeGroupMap></PluginConfiguration>", File.ReadAllText(originalFilePath));
        }

        private static PluginConfiguration CreateConfiguration(string existingMapping)
        {
            return new PluginConfiguration
            {
                EnableLlmAssist = true,
                EnableLlmEpisodeGroupMappingAssist = true,
                TmdbEpisodeGroupMap = existingMapping,
            };
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
                var mapping = obj is PluginConfiguration configuration
                    ? configuration.TmdbEpisodeGroupMap
                    : string.Empty;
                File.WriteAllText(file, $"<PluginConfiguration><TmdbEpisodeGroupMap>{mapping}</TmdbEpisodeGroupMap></PluginConfiguration>");
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

        private sealed class ThrowingXmlSerializer : RecordingXmlSerializer
        {
            private readonly Exception exception;

            public ThrowingXmlSerializer(Exception exception)
            {
                this.exception = exception;
            }

            public override void SerializeToFile(object obj, string file)
            {
                throw this.exception;
            }
        }
    }
}
