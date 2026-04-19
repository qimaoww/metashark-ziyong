using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class TvdbApiLogTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-tvdb-api-log-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        [TestInitialize]
        public void ResetConfigurationBeforeTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void ResetConfigurationAfterTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestMethod]
        public async Task GetSeriesEpisodesAsync_WhenResponseIsNonSuccess_ShouldLogChineseMessages()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTvdbSpecialsWithinSeasons = true,
                TvdbApiKey = "test-key",
            });

            var loggerStub = new Mock<ILogger<TvdbApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactory = CreateLoggerFactory(loggerStub);
            using var api = new TvdbApi(loggerFactory.Object);
            ReplaceHttpClient(
                api,
                new HttpClient(new RoutingHttpMessageHandler(CreateResponse), disposeHandler: true)
                {
                    BaseAddress = new Uri("https://api4.thetvdb.com/v4/"),
                    Timeout = TimeSpan.FromSeconds(10),
                });

            var episodes = await api.GetSeriesEpisodesAsync(321, "default", 1, "zh-CN", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, episodes.Count, "TVDB 非 2xx 时应返回空剧集列表。 ");
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["SeriesId"] = 321,
                    ["SeasonType"] = "default",
                    ["Season"] = 1,
                    ["Lang"] = "zho",
                },
                originalFormatContains: "[MetaShark] 开始获取 TVDB 剧集. 剧集ID={SeriesId} 季类型={SeasonType} 季号={Season} 语言={Lang}",
                messageContains: ["[MetaShark]", "开始获取 TVDB 剧集"]);
            AssertLoggedEventId(loggerStub, LogLevel.Debug, 3, "LogTvdbFetchEpisodes");

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["StatusCode"] = (int)HttpStatusCode.BadGateway,
                },
                originalFormatContains: "[MetaShark] TVDB 请求失败. 状态码={StatusCode}",
                messageContains: ["[MetaShark]", "TVDB 请求失败", ((int)HttpStatusCode.BadGateway).ToString()]);
            AssertLoggedEventId(loggerStub, LogLevel.Debug, 6, "LogTvdbRequestFailed");

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["StatusCode"] = (int)HttpStatusCode.BadGateway,
                    ["Url"] = "series/321/episodes/default/zho?page=0&season=1",
                },
                originalFormatContains: "[MetaShark] TVDB 请求失败. 状态码={StatusCode} 地址={Url}",
                messageContains: ["[MetaShark]", "TVDB 请求失败", "series/321/episodes/default/zho?page=0&season=1"]);
            AssertLoggedEventId(loggerStub, LogLevel.Information, 15, "LogTvdbRequestFailedInfo");

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Error,
                expectException: true,
                stateContains: new Dictionary<string, object?>
                {
                    ["Operation"] = "GetSeriesEpisodesAsync",
                },
                originalFormatContains: "[MetaShark] TVDB 请求异常. 操作={Operation}",
                messageContains: ["[MetaShark]", "TVDB 请求异常", "GetSeriesEpisodesAsync"]);
            AssertLoggedEventId(loggerStub, LogLevel.Error, 1, nameof(TvdbApi));

            static HttpResponseMessage CreateResponse(HttpRequestMessage request)
            {
                if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/login", StringComparison.Ordinal) == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":{\"token\":\"test-token\"}}", Encoding.UTF8, "application/json"),
                    };
                }

                if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/series/321/episodes/default/zho", StringComparison.Ordinal) == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadGateway)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                    };
                }

                throw new InvalidOperationException($"未预期的 TVDB 请求: {request.Method} {request.RequestUri}");
            }
        }

        private static Mock<ILoggerFactory> CreateLoggerFactory(Mock<ILogger<TvdbApi>> loggerStub)
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(loggerStub.Object);
            return loggerFactory;
        }

        private static void ReplaceHttpClient(TvdbApi api, HttpClient replacement)
        {
            var httpClientField = typeof(TvdbApi).GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(httpClientField, "TvdbApi.httpClient 未定义");

            var originalClient = httpClientField!.GetValue(api) as HttpClient;
            Assert.IsNotNull(originalClient, "TvdbApi.httpClient 不是有效的 HttpClient");

            httpClientField.SetValue(api, replacement);
            originalClient!.Dispose();
        }

        private static void EnsurePluginInstance()
        {
            if (MetaSharkPlugin.Instance != null)
            {
                EnsurePluginConfiguration();
                return;
            }

            Directory.CreateDirectory(PluginsPath);
            Directory.CreateDirectory(PluginConfigurationsPath);

            var appHost = new Mock<IServerApplicationHost>();
            appHost.Setup(x => x.GetLocalApiUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>())).Returns("http://127.0.0.1:8096");
            var applicationPaths = new Mock<IApplicationPaths>();
            applicationPaths.SetupGet(x => x.PluginsPath).Returns(PluginsPath);
            applicationPaths.SetupGet(x => x.PluginConfigurationsPath).Returns(PluginConfigurationsPath);
            var xmlSerializer = new Mock<IXmlSerializer>();

            _ = new MetaSharkPlugin(appHost.Object, applicationPaths.Object, xmlSerializer.Object);
            EnsurePluginConfiguration();
        }

        private static void EnsurePluginConfiguration()
        {
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);

            if (plugin!.Configuration != null)
            {
                return;
            }

            ReplacePluginConfiguration(new PluginConfiguration());
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

        private static void AssertLoggedEventId(Mock loggerStub, LogLevel level, int expectedId, string? expectedName)
        {
            var matches = loggerStub.Invocations
                .Where(invocation => string.Equals(invocation.Method.Name, nameof(ILogger.Log), StringComparison.Ordinal)
                    && invocation.Arguments.Count == 5
                    && invocation.Arguments[0] is LogLevel logLevel
                    && logLevel == level
                    && invocation.Arguments[1] is EventId eventId
                    && eventId.Id == expectedId
                    && string.Equals(eventId.Name, expectedName, StringComparison.Ordinal))
                .ToList();

            Assert.AreEqual(1, matches.Count, $"期望找到唯一匹配的 EventId。Level={level}, Id={expectedId}, Name={expectedName}。");
        }

        private sealed class RoutingHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

            public RoutingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                this.responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(this.responder(request));
            }
        }
    }
}
