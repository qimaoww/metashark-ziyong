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
