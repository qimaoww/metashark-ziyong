using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class LlmApiLogTest
    {
        private const string SecretKey = "sk-test-secret-123456";
        private const string RawPrompt = "raw prompt /opt/jellyfin/private/三体 S01E01.mkv";
        private const string RawResponse = "raw response containing private provider payload";

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
        public async Task CompleteAsync_WhenRequestSucceeds_ShouldLogStartedAndSucceededWithoutSensitiveValues()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(new PluginConfiguration
            {
                LlmBaseUrl = "http://localhost:11434/v1?secret=hidden",
                LlmApiKey = SecretKey,
                LlmModel = "qwen2.5",
                LlmStructuredOutputMode = PluginConfiguration.LlmStructuredOutputModeJsonSchema,
                LlmTimeoutSeconds = 5,
            });
            var loggerStub = new Mock<ILogger<LlmApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory(loggerStub).Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(_ =>
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LlmApiTest.CreateSuccessEnvelope("{\"suggestions\":[]}"), Encoding.UTF8, "application/json"),
                });
            }), disposeHandler: true));

            var result = await api.CompleteAsync(RawPrompt, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Success, result.Diagnostic);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["SchemaKind"] = LlmResponseSchemaKind.MetadataSuggestions.ToString(),
                    ["Attempt"] = 1,
                },
                originalFormatContains: "[MetaShark] LLM 请求开始. schema={SchemaKind} attempt={Attempt}",
                messageContains: ["[MetaShark]", "LLM 请求开始"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["SchemaKind"] = LlmResponseSchemaKind.MetadataSuggestions.ToString(),
                    ["Attempt"] = 1,
                },
                originalFormatContains: "[MetaShark] LLM 请求成功. schema={SchemaKind} attempt={Attempt}",
                messageContains: ["[MetaShark]", "LLM 请求成功"]);
            AssertLoggedEventId(loggerStub, LogLevel.Information, 5, "LlmApi.RequestStarted");
            AssertLoggedEventId(loggerStub, LogLevel.Information, 6, "LlmApi.RequestSucceeded");
            AssertLoggerDoesNotContainSensitiveValues(loggerStub);
            AssertLoggerDoesNotContain(loggerStub, "?secret=hidden", "/v1", "/chat/completions", "Authorization", "Cookie");
        }

        [TestMethod]
        public async Task CompleteAsync_WhenHttpErrorContainsSecretPromptAndResponse_ShouldRedactLogsAndDiagnostics()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(new PluginConfiguration
            {
                LlmBaseUrl = "http://localhost:11434/v1",
                LlmApiKey = SecretKey,
                LlmModel = "qwen2.5",
                LlmStructuredOutputMode = PluginConfiguration.LlmStructuredOutputModeJsonSchema,
                LlmTimeoutSeconds = 5,
            });
            var loggerStub = new Mock<ILogger<LlmApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory(loggerStub).Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(_ =>
            {
                var body = "{\"error\":{\"message\":\"" + SecretKey + " " + RawPrompt + " " + RawResponse + "\",\"type\":\"invalid_request_error\"}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }), disposeHandler: true));

            var result = await api.CompleteAsync(RawPrompt, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.Success);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Warning,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["StatusCode"] = (int)HttpStatusCode.BadRequest,
                },
                originalFormatContains: "[MetaShark] LLM 请求失败. 状态码={StatusCode} 诊断={Diagnostic}",
                messageContains: ["[MetaShark]", "LLM 请求失败"]);
            AssertLoggedEventId(loggerStub, LogLevel.Warning, 1, "LogLlmRequestFailed");
            AssertDoesNotContainSensitiveValues(result.Diagnostic);
            AssertLoggerDoesNotContainSensitiveValues(loggerStub);
        }

        [TestMethod]
        public async Task CompleteAsync_WhenNetworkExceptionContainsSecretPromptAndResponse_ShouldRedactLoggedException()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(new PluginConfiguration
            {
                LlmBaseUrl = "http://localhost:11434/v1",
                LlmApiKey = SecretKey,
                LlmModel = "qwen2.5",
                LlmStructuredOutputMode = PluginConfiguration.LlmStructuredOutputModeJsonSchema,
                LlmTimeoutSeconds = 5,
            });
            var loggerStub = new Mock<ILogger<LlmApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var attempts = 0;
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory(loggerStub).Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(_ =>
            {
                attempts++;
                throw new HttpRequestException(SecretKey + " " + RawPrompt + " " + RawResponse);
            }), disposeHandler: true));

            var result = await api.CompleteAsync(RawPrompt, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(3, attempts);
            LogAssert.AssertLoggedAtLeastOnce(
                loggerStub,
                LogLevel.Warning,
                expectException: true,
                stateContains: new Dictionary<string, object?>
                {
                    ["Attempt"] = 1,
                },
                originalFormatContains: "[MetaShark] LLM 请求异常，将重试. 尝试={Attempt} 诊断={Diagnostic}",
                messageContains: ["[MetaShark]", "LLM 请求异常，将重试"]);
            AssertDoesNotContainSensitiveValues(result.Diagnostic);
            AssertLoggerDoesNotContainSensitiveValues(loggerStub);
        }

        [TestMethod]
        public async Task CompleteAsync_WhenPluginTimeoutOccurs_ShouldLogWarningWithoutErrorOrSensitiveValues()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(new PluginConfiguration
            {
                LlmBaseUrl = "http://localhost:11434/v1",
                LlmApiKey = SecretKey,
                LlmModel = "qwen2.5",
                LlmStructuredOutputMode = PluginConfiguration.LlmStructuredOutputModeJsonSchema,
                LlmTimeoutSeconds = 1,
            });
            var loggerStub = new Mock<ILogger<LlmApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory(loggerStub).Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LlmApiTest.CreateSuccessEnvelope("{\"suggestions\":[]}"), Encoding.UTF8, "application/json"),
                };
            }), disposeHandler: true));

            var result = await api.CompleteAsync(RawPrompt, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.Success);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Warning,
                expectException: false,
                stateContains: new Dictionary<string, object?>(),
                originalFormatContains: "[MetaShark] LLM 请求超时. 诊断={Diagnostic}",
                messageContains: ["[MetaShark]", "LLM 请求超时"]);
            AssertLoggedEventId(loggerStub, LogLevel.Warning, 4, "LogLlmRequestTimeout");
            AssertNoErrorLogged(loggerStub);
            AssertDoesNotContainSensitiveValues(result.Diagnostic);
            AssertLoggerDoesNotContainSensitiveValues(loggerStub);
        }

        [TestMethod]
        public async Task CompleteAsync_WhenExceptionContainsFullUrlAndPath_ShouldRedactLoggedDiagnostic()
        {
            LlmApiTestSupport.ReplacePluginConfiguration(new PluginConfiguration
            {
                LlmBaseUrl = "http://localhost:11434/v1",
                LlmApiKey = SecretKey,
                LlmModel = "qwen2.5",
                LlmStructuredOutputMode = PluginConfiguration.LlmStructuredOutputModeJsonSchema,
                LlmTimeoutSeconds = 5,
            });
            var loggerStub = new Mock<ILogger<LlmApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            using var api = new LlmApi(LlmApiTestSupport.CreateLoggerFactory(loggerStub).Object);
            LlmApiTestSupport.ReplaceHttpClient(api, new HttpClient(new RoutingHttpMessageHandler(_ =>
            {
                throw new HttpRequestException("GET http://localhost:11434/v1/chat/completions failed for /opt/jellyfin/private/movie.mkv");
            }), disposeHandler: true));

            var result = await api.CompleteAsync(RawPrompt, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.Success);
            AssertLoggerDoesNotContainSensitiveValues(loggerStub);
            Assert.IsFalse(result.Diagnostic.Contains("http://localhost:11434", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsFalse(result.Diagnostic.Contains("/opt/jellyfin", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        private static void AssertLoggerDoesNotContainSensitiveValues(Mock loggerStub)
        {
            foreach (var invocation in loggerStub.Invocations.Where(invocation => string.Equals(invocation.Method.Name, nameof(ILogger.Log), StringComparison.Ordinal)))
            {
                foreach (var argument in invocation.Arguments)
                {
                    AssertDoesNotContainSensitiveValues(argument?.ToString() ?? string.Empty);
                    if (argument is Exception exception)
                    {
                        AssertDoesNotContainSensitiveValues(exception.ToString());
                    }
                }
            }
        }

        private static void AssertDoesNotContainSensitiveValues(string text)
        {
            Assert.IsFalse(text.Contains(SecretKey, StringComparison.Ordinal), text);
            Assert.IsFalse(text.Contains(RawPrompt, StringComparison.Ordinal), text);
            Assert.IsFalse(text.Contains(RawResponse, StringComparison.Ordinal), text);
            Assert.IsFalse(text.Contains("http://localhost:11434", StringComparison.OrdinalIgnoreCase), text);
            Assert.IsFalse(text.Contains("/opt/jellyfin", StringComparison.OrdinalIgnoreCase), text);
        }

        private static void AssertNoErrorLogged(Mock loggerStub)
        {
            var hasError = loggerStub.Invocations.Any(invocation => string.Equals(invocation.Method.Name, nameof(ILogger.Log), StringComparison.Ordinal)
                && invocation.Arguments.Count == 5
                && invocation.Arguments[0] is LogLevel logLevel
                && logLevel == LogLevel.Error);
            Assert.IsFalse(hasError, "LLM optional timeout/network failures must not be logged as Error.");
        }

        private static void AssertLoggerDoesNotContain(Mock loggerStub, params string[] forbiddenValues)
        {
            foreach (var invocation in loggerStub.Invocations.Where(invocation => string.Equals(invocation.Method.Name, nameof(ILogger.Log), StringComparison.Ordinal)))
            {
                foreach (var argument in invocation.Arguments)
                {
                    var text = argument?.ToString() ?? string.Empty;
                    foreach (var forbiddenValue in forbiddenValues)
                    {
                        Assert.IsFalse(text.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase), text);
                    }
                }
            }
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
    }
}
