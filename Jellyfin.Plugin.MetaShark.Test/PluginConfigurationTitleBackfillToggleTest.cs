using System.Reflection;
using Jellyfin.Plugin.MetaShark.Configuration;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class PluginConfigurationTitleBackfillToggleTest
    {
        private const string PropertyName = "EnableSearchMissingMetadataEpisodeTitleBackfill";

        [TestMethod]
        public void ShouldDefaultTitleBackfillToggleToFalseOnNewConfiguration()
        {
            var configuration = new PluginConfiguration();
            var property = typeof(PluginConfiguration).GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(property, $"PluginConfiguration.{PropertyName} 未定义");
            Assert.AreEqual(typeof(bool), property.PropertyType);
            Assert.AreEqual(false, property.GetValue(configuration));
        }

        [TestMethod]
        public void ShouldReadBackTrueAfterSettingTitleBackfillToggle()
        {
            var configuration = new PluginConfiguration();
            var property = typeof(PluginConfiguration).GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(property, $"PluginConfiguration.{PropertyName} 未定义");

            property.SetValue(configuration, true);

            Assert.AreEqual(true, property.GetValue(configuration));
        }

        [TestMethod]
        public void ShouldReadBackFalseAfterSettingTitleBackfillToggle()
        {
            var configuration = new PluginConfiguration();
            var property = typeof(PluginConfiguration).GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(property, $"PluginConfiguration.{PropertyName} 未定义");

            property.SetValue(configuration, false);

            Assert.AreEqual(false, property.GetValue(configuration));
            Assert.IsTrue(configuration.EnableTmdb);
        }

        [TestMethod]
        public void ShouldKeepEnableTmdbDefaultTrueAfterSettingTitleBackfillToggle()
        {
            var configuration = new PluginConfiguration();
            var property = typeof(PluginConfiguration).GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(property, $"PluginConfiguration.{PropertyName} 未定义");

            property.SetValue(configuration, true);

            Assert.IsTrue(configuration.EnableTmdb);
        }
    }
}
