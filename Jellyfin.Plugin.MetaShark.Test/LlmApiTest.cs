using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class LlmApiTest
    {
        [TestInitialize]
        public void ResetConfigurationBeforeTest()
        {
            LlmApiTestSupport.EnsurePluginInstance();
            LlmApiTestSupport.ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void ResetConfigurationAfterTest()
        {
            LlmApiTestSupport.EnsurePluginInstance();
            LlmApiTestSupport.ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestMethod]
        public async Task CompleteAsync_WhenConfigured_ShouldUseChatCompletionsUrlAuthorizationAndBodyWhitelist()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(new PluginConfiguration
            {
                LlmBaseUrl = "http://localhost:11434/v1/",
                LlmApiKey = "test-key",
                LlmModel = "qwen2.5",
                LlmMaxTokens = 777,
                LlmStructuredOutputMode = PluginConfiguration.LlmStructuredOutputModeJsonSchema,
                LlmTimeoutSeconds = 5,
            });

            HttpRequestMessage? capturedRequest = null;
            string? capturedBody = null;
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory<LlmApi>().Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(async request =>
            {
                capturedRequest = request;
                capturedBody = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
                return CreateJsonResponse(HttpStatusCode.OK, CreateSuccessEnvelope("{\"title\":\"三体\",\"confidence\":0.91}"));
            }), disposeHandler: true));

            var result = await api.CompleteAsync("请根据安全上下文输出 JSON", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.IsNotNull(capturedRequest);
            Assert.AreEqual(HttpMethod.Post, capturedRequest!.Method);
            Assert.AreEqual("http://localhost:11434/v1/chat/completions", capturedRequest.RequestUri!.ToString());
            Assert.AreEqual("application/json", capturedRequest.Content!.Headers.ContentType!.MediaType);
            Assert.AreEqual("Bearer", capturedRequest.Headers.Authorization!.Scheme);
            Assert.AreEqual("test-key", capturedRequest.Headers.Authorization.Parameter);

            using var bodyDocument = JsonDocument.Parse(capturedBody!);
            var root = bodyDocument.RootElement;
            CollectionAssert.AreEquivalent(
                new[] { "model", "messages", "temperature", "max_tokens", "response_format" },
                root.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual("qwen2.5", root.GetProperty("model").GetString());
            Assert.AreEqual(0D, root.GetProperty("temperature").GetDouble());
            Assert.AreEqual(777, root.GetProperty("max_tokens").GetInt32());
            Assert.AreEqual(JsonValueKind.Array, root.GetProperty("messages").ValueKind);
            Assert.AreEqual("json_schema", root.GetProperty("response_format").GetProperty("type").GetString());
            AssertForbiddenRequestFieldsAbsent(root);
        }

        [TestMethod]
        public async Task CompleteAsync_WhenModeIsJsonObject_ShouldSendJsonObjectResponseFormat()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(CreateConfiguration(PluginConfiguration.LlmStructuredOutputModeJsonObject));
            string? capturedBody = null;
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory<LlmApi>().Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(async request =>
            {
                capturedBody = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
                return CreateJsonResponse(HttpStatusCode.OK, CreateSuccessEnvelope("{\"title\":\"三体\",\"confidence\":0.9}"));
            }), disposeHandler: true));

            var result = await api.CompleteAsync("prompt", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Success, result.Diagnostic);
            using var bodyDocument = JsonDocument.Parse(capturedBody!);
            Assert.AreEqual("json_object", bodyDocument.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        }

        [TestMethod]
        public async Task CompleteAsync_WhenModeIsTextJson_ShouldNotSendResponseFormat()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(CreateConfiguration(PluginConfiguration.LlmStructuredOutputModeTextJson));
            string? capturedBody = null;
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory<LlmApi>().Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(async request =>
            {
                capturedBody = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
                return CreateJsonResponse(HttpStatusCode.OK, CreateSuccessEnvelope("这里是结果 {\"title\":\"三体\",\"confidence\":0.9}"));
            }), disposeHandler: true));

            var result = await api.CompleteAsync("prompt", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Success, result.Diagnostic);
            using var bodyDocument = JsonDocument.Parse(capturedBody!);
            Assert.IsFalse(bodyDocument.RootElement.TryGetProperty("response_format", out _), capturedBody);
        }

        [TestMethod]
        public async Task CompleteAsync_WhenTransientHttpStatusesOccur_ShouldRetryTwiceThenSucceed()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(CreateConfiguration(PluginConfiguration.LlmStructuredOutputModeJsonSchema));
            var responses = new Queue<HttpStatusCode>(new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.OK });
            var attempts = 0;
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory<LlmApi>().Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(_ =>
            {
                attempts++;
                var statusCode = responses.Dequeue();
                return Task.FromResult(statusCode == HttpStatusCode.OK
                    ? CreateJsonResponse(statusCode, CreateSuccessEnvelope("{\"title\":\"三体\",\"confidence\":0.9}"))
                    : CreateJsonResponse(statusCode, "{\"error\":{\"message\":\"temporary\",\"type\":\"server\"}}"));
            }), disposeHandler: true));

            var result = await api.CompleteAsync("prompt", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual(3, attempts, "最多 2 次 retry 应对应总尝试 3 次。 ");
        }

        [TestMethod]
        public async Task CompleteAsync_WhenBadRequestOccurs_ShouldNotRetry()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(CreateConfiguration(PluginConfiguration.LlmStructuredOutputModeJsonSchema));
            var attempts = 0;
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory<LlmApi>().Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(_ =>
            {
                attempts++;
                return Task.FromResult(CreateJsonResponse(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"bad request\",\"type\":\"invalid_request_error\",\"param\":\"messages\",\"code\":\"bad_json\"}}"));
            }), disposeHandler: true));

            var result = await api.CompleteAsync("prompt", CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(1, attempts);
            Assert.IsTrue(result.Diagnostic.Contains("bad request", StringComparison.Ordinal), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("invalid_request_error", StringComparison.Ordinal), result.Diagnostic);
        }

        [TestMethod]
        public async Task CompleteAsync_WhenCallerCancels_ShouldNotRetryOrWrapCancellation()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(CreateConfiguration(PluginConfiguration.LlmStructuredOutputModeJsonSchema));
            var attempts = 0;
            using var cancellationTokenSource = new CancellationTokenSource();
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory<LlmApi>().Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(_ =>
            {
                attempts++;
                cancellationTokenSource.Cancel();
                throw new OperationCanceledException(cancellationTokenSource.Token);
            }), disposeHandler: true));

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => api.CompleteAsync("prompt", cancellationTokenSource.Token)).ConfigureAwait(false);
            Assert.AreEqual(1, attempts);
        }

        private static PluginConfiguration CreateConfiguration(string structuredOutputMode)
        {
            return new PluginConfiguration
            {
                LlmBaseUrl = "http://localhost:11434/v1",
                LlmModel = "qwen2.5",
                LlmMaxTokens = 512,
                LlmStructuredOutputMode = structuredOutputMode,
                LlmTimeoutSeconds = 5,
            };
        }

        private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        internal static string CreateSuccessEnvelope(string content)
        {
            return JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content,
                        },
                        finish_reason = "stop",
                    },
                },
            });
        }

        private static void AssertForbiddenRequestFieldsAbsent(JsonElement root)
        {
            foreach (var fieldName in new[] { "user", "n", "tool_choice", "logit_bias", "tools", "stream" })
            {
                Assert.IsFalse(root.TryGetProperty(fieldName, out _), $"生产请求不应包含字段 {fieldName}: {root}");
            }
        }
    }

    internal static class LlmApiTestSupport
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-llm-api-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        public static Mock<ILoggerFactory> CreateLoggerFactory<T>(Mock<ILogger<T>>? loggerStub = null)
            where T : class
        {
            loggerStub ??= new Mock<ILogger<T>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(loggerStub.Object);
            return loggerFactory;
        }

        public static void ReplaceHttpClient(LlmApi api, HttpClient replacement)
        {
            var httpClientField = typeof(LlmApi).GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(httpClientField, "LlmApi.httpClient 未定义");

            var originalClient = httpClientField!.GetValue(api) as HttpClient;
            Assert.IsNotNull(originalClient, "LlmApi.httpClient 不是有效的 HttpClient");

            httpClientField.SetValue(api, replacement);
            originalClient!.Dispose();
        }

        public static void EnsurePluginInstance()
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

        public static void ReplacePluginConfiguration(PluginConfiguration configuration)
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
    }

    internal sealed class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> responder;

        public RoutingHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return this.responder(request);
        }
    }
}
