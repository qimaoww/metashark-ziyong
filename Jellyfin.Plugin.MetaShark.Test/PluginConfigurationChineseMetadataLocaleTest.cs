using Jellyfin.Plugin.MetaShark.Configuration;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class PluginConfigurationChineseMetadataLocaleTest
    {
        [TestMethod]
        public void ShouldDefaultToZhCn()
        {
            Assert.AreEqual("zh-CN", new PluginConfiguration().DefaultChineseMetadataLocale);
        }

        [DataTestMethod]
        [DataRow("zh-CN")]
        [DataRow("zh-SG")]
        [DataRow("zh-TW")]
        [DataRow("zh-HK")]
        public void ShouldKeepSupportedLocale(string value)
        {
            var configuration = new PluginConfiguration { DefaultChineseMetadataLocale = value };

            Assert.AreEqual(value, configuration.DefaultChineseMetadataLocale);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("zh-Hans")]
        [DataRow("invalid")]
        public void ShouldNormalizeUnsupportedLocaleToZhCn(string? value)
        {
            var configuration = new PluginConfiguration { DefaultChineseMetadataLocale = value! };

            Assert.AreEqual("zh-CN", configuration.DefaultChineseMetadataLocale);
        }
    }
}
