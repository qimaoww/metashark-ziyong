using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class BaseProviderProxyImageUrlTest
    {
        private const string OriginalImageUrl = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p1234567890.webp";
        private static readonly string TestRootPath = Path.Combine(Path.GetTempPath(), "metashark-base-provider-proxy-url-test");
        private static readonly string PluginsPath = Path.Combine(TestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(TestRootPath, "configurations");

        [TestInitialize]
        public void TestInitialize()
        {
            SetPluginInstance(null);
            Directory.CreateDirectory(PluginsPath);
            Directory.CreateDirectory(PluginConfigurationsPath);

            var appHost = new Mock<IServerApplicationHost>();
            appHost
                .Setup(x => x.GetLocalApiUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
                .Returns<string, string, int?>((host, scheme, port) =>
                {
                    var effectivePort = port.HasValue && port.Value > 0 ? port.Value : 8096;
                    return $"{scheme}://{host}:{effectivePort}/";
                });

            var applicationPaths = new Mock<IApplicationPaths>();
            applicationPaths.SetupGet(x => x.PluginsPath).Returns(PluginsPath);
            applicationPaths.SetupGet(x => x.PluginConfigurationsPath).Returns(PluginConfigurationsPath);
            var xmlSerializer = new Mock<IXmlSerializer>();

            _ = new MetaSharkPlugin(appHost.Object, applicationPaths.Object, xmlSerializer.Object);
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void TestCleanup()
        {
            SetPluginInstance(null);
        }

        [TestMethod]
        public void GetLocalProxyImageUrl_ShouldNormalizeLocalBaseUrlAndRouteShape()
        {
            var result = BaseProviderProbe.InvokeGetLocalProxyImageUrl(new Uri(OriginalImageUrl, UriKind.Absolute));

            AssertNormalizedProxyUrl(result, "http://127.0.0.1:8096");
        }

        [TestMethod]
        public void GetProxyImageUrl_ShouldNormalizeRequestBaseUrlAndRouteShape()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("jellyfin.example.com", 8096);

            var provider = new BaseProviderProbe(new HttpContextAccessor { HttpContext = httpContext });
            var result = provider.InvokeGetProxyImageUrl(new Uri(OriginalImageUrl, UriKind.Absolute));

            AssertNormalizedProxyUrl(result, "http://jellyfin.example.com:8096");
        }

        private static void AssertNormalizedProxyUrl(Uri result, string expectedBaseUrl)
        {
            Assert.AreEqual("/plugin/metashark/proxy/image", result.AbsolutePath);
            Assert.AreEqual(OriginalImageUrl, HttpUtility.ParseQueryString(result.Query).Get("url"));
            StringAssert.StartsWith(result.ToString(), expectedBaseUrl + "/plugin/metashark/proxy/image?url=");
            Assert.IsFalse(result.ToString().Contains("//plugin/", StringComparison.Ordinal), "代理 URL 不应包含双斜杠 plugin 路径段。");
            Assert.IsFalse(result.ToString().Contains("/proxy/image/?", StringComparison.Ordinal), "代理 URL 不应在 image 与查询串之间多出 '/'.");
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

            Assert.Fail("Could not replace MetaSharkPlugin configuration for tests.");
        }

        private static void SetPluginInstance(MetaSharkPlugin? plugin)
        {
            var instanceProperty = typeof(MetaSharkPlugin).GetProperty(nameof(MetaSharkPlugin.Instance), BindingFlags.Static | BindingFlags.Public);
            var setMethod = instanceProperty?.GetSetMethod(true);
            Assert.IsNotNull(setMethod);
            setMethod!.Invoke(null, new object?[] { plugin });
        }

        private sealed class BaseProviderProbe : BaseProvider
        {
            public BaseProviderProbe(IHttpContextAccessor httpContextAccessor)
                : base(null!, null!, null!, httpContextAccessor, null!, null!, null!, null!)
            {
            }

            public Uri InvokeGetProxyImageUrl(Uri url)
            {
                return this.GetProxyImageUrl(url);
            }

            public static Uri InvokeGetLocalProxyImageUrl(Uri url)
            {
                return GetLocalProxyImageUrl(url);
            }
        }
    }
}
