using System.Reflection;
using Jellyfin.Plugin.MetaShark.Configuration;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class PluginConfigurationTitleBackfillToggleTest
    {
        private const string PropertyName = "EnableSearchMissingMetadataEpisodeTitleBackfill";
        private const string CheckboxId = "EnableSearchMissingMetadataEpisodeTitleBackfill";

        private static readonly string ConfigPagePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../Jellyfin.Plugin.MetaShark/Configuration/configPage.html"));

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

        [TestMethod]
        public void ConfigPage_ShouldBindTitleBackfillToggleOnPageShowAndSubmit()
        {
            var html = ReadConfigPageHtml();
            var loadBinding = $"document.querySelector('#{CheckboxId}').checked = config.{PropertyName};";
            var saveBinding = $"config.{PropertyName} = document.querySelector('#{CheckboxId}').checked;";

            Assert.AreEqual(2, CountOccurrences(html, $"config.{PropertyName}"), $"{PropertyName} 在配置页脚本中应只用于一次读取和一次保存绑定。");
            Assert.IsTrue(html.Contains(loadBinding, StringComparison.Ordinal), $"pageshow 时必须从 config.{PropertyName} 回填 checkbox.checked。");
            Assert.IsTrue(html.Contains(saveBinding, StringComparison.Ordinal), $"submit 时必须把 checkbox.checked 回写到 config.{PropertyName}。");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ConfigPage_ShouldRoundTripTitleBackfillToggleForFalseAndTrue(bool configuredValue)
        {
            var pageState = SimulateCheckboxRoundTrip(configuredValue);

            Assert.AreEqual(configuredValue, pageState.LoadedChecked, "pageshow 必须把配置值原样回填到 checkbox.checked。");
            Assert.AreEqual(configuredValue, pageState.SavedValue, "submit 必须把 checkbox.checked 原样保存回配置对象。");
        }

        private static string ReadConfigPageHtml()
        {
            return File.ReadAllText(ConfigPagePath);
        }

        private static (bool LoadedChecked, bool SavedValue) SimulateCheckboxRoundTrip(bool configuredValue)
        {
            var checkboxChecked = configuredValue;
            var savedValue = checkboxChecked;
            return (checkboxChecked, savedValue);
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            var startIndex = 0;

            while (true)
            {
                var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
                if (index < 0)
                {
                    return count;
                }

                count++;
                startIndex = index + value.Length;
            }
        }
    }
}
