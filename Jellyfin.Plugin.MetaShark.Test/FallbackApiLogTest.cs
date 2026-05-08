using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Objects.Exceptions;
using TMDbLib.Objects.General;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class FallbackApiLogTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-fallback-api-log-tests");
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
        public async Task GetMovieAsync_WhenTmdbConnectionFails_ShouldLogChineseError()
        {
            var unusedPort = GetUnusedLoopbackPort();
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbApiKey = "test-key",
                TmdbHost = $"http://127.0.0.1:{unusedPort}",
            });

            var loggerStub = new Mock<ILogger<TmdbApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactory = CreateLoggerFactory(loggerStub);
            using var api = new TmdbApi(loggerFactory.Object);

            var result = await api.GetMovieAsync(123, "zh-CN", "zh-CN", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "TMDB 连接失败时应返回 null。 ");
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Error,
                expectException: true,
                stateContains: new Dictionary<string, object?>
                {
                    ["Operation"] = nameof(TmdbApi.GetMovieAsync),
                },
                originalFormatContains: "[MetaShark] TMDB 请求异常. 操作={Operation}",
                messageContains: ["[MetaShark]", "TMDB 请求异常", nameof(TmdbApi.GetMovieAsync)]);
            AssertLoggedEventId(loggerStub, LogLevel.Error, 1, nameof(TmdbApi));
        }


        [TestMethod]
        public async Task GetEpisodeAsync_WhenTmdbGeneralHttpException_ShouldLogWarningAndReturnNull()
        {
            var loggerStub = new Mock<ILogger<TmdbApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactory = CreateLoggerFactory(loggerStub);
            using var api = CreateTmdbApiWithStatus(loggerFactory.Object, HttpStatusCode.InternalServerError);

            var result = await api.GetEpisodeAsync(1, 2, 3, "zh-CN", "zh-CN", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "TMDB 单集详情遇到非预期 HTTP 错误时应返回 null。 ");
            AssertTmdbUnexpectedHttpWarningLogged(loggerStub, nameof(TmdbApi.GetEpisodeAsync));
        }

        [TestMethod]
        public async Task GetEpisodeTranslationTitleAsync_WhenTmdbGeneralHttpException_ShouldLogWarningAndReturnNull()
        {
            var loggerStub = new Mock<ILogger<TmdbApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactory = CreateLoggerFactory(loggerStub);
            using var api = CreateTmdbApiWithStatus(loggerFactory.Object, HttpStatusCode.InternalServerError);

            var result = await api.GetEpisodeTranslationTitleAsync(1, 2, 3, "zh-CN", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "TMDB 单集翻译标题遇到非预期 HTTP 错误时应返回 null。 ");
            AssertTmdbUnexpectedHttpWarningLogged(loggerStub, nameof(TmdbApi.GetEpisodeTranslationTitleAsync));
        }

        [TestMethod]
        public async Task GetEpisodeTranslationOverviewAsync_WhenTmdbGeneralHttpException_ShouldLogWarningAndReturnNull()
        {
            var loggerStub = new Mock<ILogger<TmdbApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactory = CreateLoggerFactory(loggerStub);
            using var api = CreateTmdbApiWithStatus(loggerFactory.Object, HttpStatusCode.InternalServerError);

            var result = await api.GetEpisodeTranslationOverviewAsync(1, 2, 3, "zh-CN", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "TMDB 单集翻译简介遇到非预期 HTTP 错误时应返回 null。 ");
            AssertTmdbUnexpectedHttpWarningLogged(loggerStub, nameof(TmdbApi.GetEpisodeTranslationOverviewAsync));
        }

        [TestMethod]
        public async Task GetEpisodeImagesAsync_WhenTmdbGeneralHttpException_ShouldLogWarningAndReturnNull()
        {
            var loggerStub = new Mock<ILogger<TmdbApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactory = CreateLoggerFactory(loggerStub);
            using var api = CreateTmdbApiWithStatus(loggerFactory.Object, HttpStatusCode.InternalServerError);

            var result = await api.GetEpisodeImagesAsync(1, 2, 3, "zh-CN", string.Empty, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "TMDB 单集图片遇到非预期 HTTP 错误时应返回 null。 ");
            AssertTmdbUnexpectedHttpWarningLogged(loggerStub, nameof(TmdbApi.GetEpisodeImagesAsync));
        }

        [TestMethod]
        public async Task CheckNewIDAsync_WhenHttpRequestFails_ShouldLogChineseError()
        {
            var loggerStub = new Mock<ILogger<ImdbApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactory = CreateLoggerFactory(loggerStub);
            using var api = new ImdbApi(loggerFactory.Object);
            ReplaceHttpClient(api, "httpClient", new HttpClient(new ThrowingHttpMessageHandler(new HttpRequestException("boom")), disposeHandler: true));

            var imdbId = "tt1234567";
            var result = await api.CheckNewIDAsync(imdbId, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "IMDB 请求失败时应返回 null。 ");
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Error,
                expectException: true,
                stateContains: new Dictionary<string, object?>
                {
                    ["ImdbId"] = imdbId,
                },
                originalFormatContains: "[MetaShark] 检查 IMDB 新 ID 失败. IMDB编号={ImdbId}",
                messageContains: ["[MetaShark]", "检查 IMDB 新 ID 失败", imdbId]);
            AssertLoggedEventId(loggerStub, LogLevel.Error, 1, nameof(ImdbApi.CheckNewIDAsync));
        }

        [TestMethod]
        public async Task CheckPersonNewIDAsync_WhenHttpRequestFails_ShouldLogChineseError()
        {
            var loggerStub = new Mock<ILogger<ImdbApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactory = CreateLoggerFactory(loggerStub);
            using var api = new ImdbApi(loggerFactory.Object);
            ReplaceHttpClient(api, "httpClient", new HttpClient(new ThrowingHttpMessageHandler(new HttpRequestException("boom")), disposeHandler: true));

            var imdbId = "nm1234567";
            var result = await api.CheckPersonNewIDAsync(imdbId, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "IMDB 人物请求失败时应返回 null。 ");
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Error,
                expectException: true,
                stateContains: new Dictionary<string, object?>
                {
                    ["ImdbId"] = imdbId,
                },
                originalFormatContains: "[MetaShark] 检查人物 IMDB 新 ID 失败. IMDB编号={ImdbId}",
                messageContains: ["[MetaShark]", "检查人物 IMDB 新 ID 失败", imdbId]);
            AssertLoggedEventId(loggerStub, LogLevel.Error, 2, nameof(ImdbApi.CheckPersonNewIDAsync));
        }

        [TestMethod]
        public async Task GetByImdbID_WhenHttpRequestFails_ShouldLogChineseError()
        {
            var loggerStub = new Mock<ILogger<OmdbApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactory = CreateLoggerFactory(loggerStub);
            using var api = new OmdbApi(loggerFactory.Object);
            ReplaceHttpClient(api, "httpClient", new HttpClient(new ThrowingHttpMessageHandler(new HttpRequestException("boom")), disposeHandler: true));

            var imdbId = "tt7654321";
            var result = await api.GetByImdbID(imdbId, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "OMDB 请求失败时应返回 null。 ");
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Error,
                expectException: true,
                stateContains: new Dictionary<string, object?>
                {
                    ["ImdbId"] = imdbId,
                },
                originalFormatContains: "[MetaShark] 通过 IMDB ID 获取 OMDB 数据失败. IMDB编号={ImdbId}",
                messageContains: ["[MetaShark]", "通过 IMDB ID 获取 OMDB 数据失败", imdbId]);
            AssertLoggedEventId(loggerStub, LogLevel.Error, 1, nameof(OmdbApi.GetByImdbID));
        }


        private static TmdbApi CreateTmdbApiWithStatus(ILoggerFactory loggerFactory, HttpStatusCode statusCode)
        {
            var api = new TmdbApi(loggerFactory);
            ConfigureTmdbClient(api, new HttpClient(new StatusHttpMessageHandler(statusCode)));
            return api;
        }

        private static void ConfigureTmdbClient(TmdbApi api, HttpClient httpClient)
        {
            var tmdbClientField = typeof(TmdbApi).GetField("tmDbClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(tmdbClientField, "TmdbApi.tmDbClient 未定义。 ");
            var tmdbClient = tmdbClientField!.GetValue(api);
            Assert.IsNotNull(tmdbClient, "TmdbApi.tmDbClient 不是有效对象。 ");
            var setConfigMethod = tmdbClient!.GetType().GetMethod("SetConfig", new[] { typeof(TMDbConfig) });
            Assert.IsNotNull(setConfigMethod, "TMDbClient.SetConfig 未定义。 ");
            setConfigMethod!.Invoke(tmdbClient, new object[] { new TMDbConfig() });

            var restClientField = tmdbClient.GetType().GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(restClientField, "TMDbClient._client 未定义。 ");
            var restClient = restClientField!.GetValue(tmdbClient);
            Assert.IsNotNull(restClient, "TMDbClient._client 不是有效对象。 ");
            var httpClientProperty = restClient!.GetType().GetProperty("HttpClient", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(httpClientProperty, "RestClient.HttpClient 未定义。 ");
            httpClientProperty!.SetValue(restClient, httpClient);
        }

        private static void AssertTmdbUnexpectedHttpWarningLogged(Mock<ILogger<TmdbApi>> loggerStub, string operation)
        {
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Warning,
                expectException: true,
                stateContains: new Dictionary<string, object?>
                {
                    ["Operation"] = operation,
                },
                originalFormatContains: "[MetaShark] TMDB 单集请求返回非预期 HTTP 错误. 操作={Operation}",
                messageContains: ["[MetaShark]", "TMDB 单集请求返回非预期 HTTP 错误", operation]);
            AssertLoggedEventId(loggerStub, LogLevel.Warning, 2, nameof(TmdbApi));
            AssertLoggedExceptionType<GeneralHttpException>(loggerStub, LogLevel.Warning);
            AssertNoErrorLogged(loggerStub, operation);
        }

        private static void AssertNoErrorLogged(Mock loggerStub, string operation)
        {
            var hasError = loggerStub.Invocations.Any(invocation => string.Equals(invocation.Method.Name, nameof(ILogger.Log), StringComparison.Ordinal)
                && invocation.Arguments.Count == 5
                && invocation.Arguments[0] is LogLevel logLevel
                && logLevel == LogLevel.Error
                && invocation.Arguments[2]?.ToString()?.Contains(operation, StringComparison.Ordinal) == true);
            Assert.IsFalse(hasError, $"TMDB single-episode GeneralHttpException should be Warning-only for {operation}.");
        }

        private static void AssertLoggedExceptionType<TException>(Mock loggerStub, LogLevel level)
            where TException : Exception
        {
            var matches = loggerStub.Invocations
                .Where(invocation => string.Equals(invocation.Method.Name, nameof(ILogger.Log), StringComparison.Ordinal)
                    && invocation.Arguments.Count == 5
                    && invocation.Arguments[0] is LogLevel logLevel
                    && logLevel == level
                    && invocation.Arguments[3] is TException)
                .ToList();

            Assert.AreEqual(1, matches.Count, $"期望找到唯一 {typeof(TException).Name} 异常日志。Level={level}。");
        }

        private static Mock<ILoggerFactory> CreateLoggerFactory<T>(Mock<ILogger<T>> loggerStub)
            where T : class
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(loggerStub.Object);
            return loggerFactory;
        }

        private static void ReplaceHttpClient(object api, string fieldName, HttpClient replacement)
        {
            var httpClientField = api.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(httpClientField, $"{api.GetType().Name}.{fieldName} 未定义");

            var originalClient = httpClientField!.GetValue(api) as HttpClient;
            Assert.IsNotNull(originalClient, $"{api.GetType().Name}.{fieldName} 不是有效的 HttpClient");

            httpClientField.SetValue(api, replacement);
            originalClient!.Dispose();
        }

        private static int GetUnusedLoopbackPort()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
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


        private sealed class StatusHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode statusCode;

            public StatusHttpMessageHandler(HttpStatusCode statusCode)
            {
                this.statusCode = statusCode;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(this.statusCode)
                {
                    Content = new StringContent("server error"),
                });
            }
        }

        private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
        {
            private readonly Exception exception;

            public ThrowingHttpMessageHandler(Exception exception)
            {
                this.exception = exception;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromException<HttpResponseMessage>(this.exception);
            }
        }
    }
}
