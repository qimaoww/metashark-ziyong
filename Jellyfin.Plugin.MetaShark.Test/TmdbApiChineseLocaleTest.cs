using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class TmdbApiChineseLocaleTest
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "metashark-tmdb-chinese-locale-tests");
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestInitialize]
        public void Initialize()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void Cleanup()
        {
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [DataTestMethod]
        [DataRow("zh", "TW", "zh-CN", "zh-TW")]
        [DataRow("zh", "AU", "zh-HK", "zh-HK")]
        [DataRow("zh-Hant", "HK", "zh-CN", "zh-HK")]
        [DataRow("zh-Hans", "SG", "zh-TW", "zh-SG")]
        [DataRow("en-us", "CN", "zh-CN", "en-US")]
        public void ResolveMetadataLanguage_UsesCountryAndConfiguredDefault(string language, string? countryCode, string configuredDefault, string expected)
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = configuredDefault,
            });
            using var api = new TmdbApi(this.loggerFactory);

            Assert.AreEqual(expected, api.ResolveMetadataLanguage(language, countryCode));
        }

        [TestMethod]
        public void ImageLanguages_KeepGenericZhCompatibility()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                DefaultChineseMetadataLocale = "zh-TW",
            });
            var method = typeof(TmdbApi).GetMethod("GetImageLanguagesParam", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            var result = method!.Invoke(null, new object[] { "zh" }) as string;

            Assert.AreEqual("zh,null,en", result);
        }

        private static void EnsurePluginInstance()
        {
            if (MetaSharkPlugin.Instance != null)
            {
                return;
            }

            var pluginsPath = Path.Combine(Root, "plugins");
            var configurationsPath = Path.Combine(Root, "configurations");
            Directory.CreateDirectory(pluginsPath);
            Directory.CreateDirectory(configurationsPath);
            var appHost = new Mock<IServerApplicationHost>();
            appHost.Setup(x => x.GetLocalApiUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
                .Returns("http://127.0.0.1:8096");
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(x => x.PluginsPath).Returns(pluginsPath);
            paths.SetupGet(x => x.PluginConfigurationsPath).Returns(configurationsPath);
            _ = new MetaSharkPlugin(appHost.Object, paths.Object, new Mock<IXmlSerializer>().Object);
        }

        private static void ReplacePluginConfiguration(PluginConfiguration configuration)
        {
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            var type = plugin!.GetType();
            while (type != null)
            {
                var property = type.GetProperty("Configuration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.SetMethod != null && property.PropertyType.IsAssignableFrom(typeof(PluginConfiguration)))
                {
                    property.SetValue(plugin, configuration);
                    return;
                }

                var field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => candidate.FieldType.IsAssignableFrom(typeof(PluginConfiguration)));
                if (field != null)
                {
                    field.SetValue(plugin, configuration);
                    return;
                }

                type = type.BaseType;
            }

            Assert.Fail("Could not replace plugin configuration.");
        }
    }
}
