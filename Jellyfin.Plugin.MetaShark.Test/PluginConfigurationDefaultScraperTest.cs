using System.Reflection;
using Jellyfin.Plugin.MetaShark.Configuration;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class PluginConfigurationDefaultScraperTest
    {
        private const string PropertyName = "DefaultScraperMode";
        private const string DefaultMode = "default";
        private const string TmdbOnlyMode = "tmdb-only";

        [TestMethod]
        public void ShouldDefaultScraperModeToDefaultOnNewConfiguration()
        {
            var configuration = new PluginConfiguration();
            var property = typeof(PluginConfiguration).GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(property, $"PluginConfiguration.{PropertyName} 未定义");
            Assert.AreEqual(typeof(string), property.PropertyType);
            Assert.AreEqual(DefaultMode, property.GetValue(configuration));
        }

        [TestMethod]
        public void ShouldReadBackTmdbOnlyModeAfterSettingSupportedValue()
        {
            var configuration = new PluginConfiguration();
            var property = typeof(PluginConfiguration).GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(property, $"PluginConfiguration.{PropertyName} 未定义");

            property.SetValue(configuration, TmdbOnlyMode);

            Assert.AreEqual(TmdbOnlyMode, property.GetValue(configuration));
        }

        [TestMethod]
        public void ShouldNormalizeEmptyScraperModeBackToDefault()
        {
            var configuration = new PluginConfiguration();
            var property = typeof(PluginConfiguration).GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(property, $"PluginConfiguration.{PropertyName} 未定义");

            property.SetValue(configuration, string.Empty);

            Assert.AreEqual(DefaultMode, property.GetValue(configuration));
        }

        [TestMethod]
        public void ShouldNormalizeInvalidScraperModeBackToDefault()
        {
            var configuration = new PluginConfiguration();
            var property = typeof(PluginConfiguration).GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(property, $"PluginConfiguration.{PropertyName} 未定义");

            property.SetValue(configuration, "invalid-mode");

            Assert.AreEqual(DefaultMode, property.GetValue(configuration));
        }

        [TestMethod]
        public void ShouldKeepExistingEnableTmdbDefaultsUnchanged()
        {
            var configuration = new PluginConfiguration();
            var property = typeof(PluginConfiguration).GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(property, $"PluginConfiguration.{PropertyName} 未定义");

            property.SetValue(configuration, TmdbOnlyMode);

            Assert.IsTrue(configuration.EnableTmdb);
            Assert.IsFalse(configuration.EnableTmdbSearch);
            Assert.IsTrue(configuration.EnableTmdbMatch);
        }
    }
}
