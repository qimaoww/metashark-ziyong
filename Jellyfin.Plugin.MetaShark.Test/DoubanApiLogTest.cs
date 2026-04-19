using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class DoubanApiLogTest
    {
        [TestMethod]
        public async Task SearchAsync_WhenResponseIsNonSuccess_ShouldLogChineseWarning()
        {
            var loggerStub = new Mock<ILogger<DoubanApi>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactory = CreateLoggerFactory(loggerStub);
            using var api = new DoubanApi(loggerFactory.Object);
            ReplaceHttpClient(api, new HttpClient(new StaticResponseHandler(HttpStatusCode.BadGateway, "bad gateway"), disposeHandler: true));

            var keyword = "bad keyword";
            var result = await api.SearchAsync(keyword, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, result.Count, "Douban 非 2xx 时应直接返回空结果。 ");
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Warning,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Keyword"] = keyword,
                    ["StatusCode"] = HttpStatusCode.BadGateway,
                },
                originalFormatContains: "[MetaShark] Douban 搜索请求失败. 关键词={Keyword} 状态码={StatusCode}",
                messageContains: ["[MetaShark]", "Douban 搜索请求失败", keyword, HttpStatusCode.BadGateway.ToString()]);
            AssertLoggedEventId(loggerStub, LogLevel.Warning, 2, nameof(DoubanApi.SearchAsync));
        }

        private static Mock<ILoggerFactory> CreateLoggerFactory(Mock<ILogger<DoubanApi>> loggerStub)
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(loggerStub.Object);
            return loggerFactory;
        }

        private static void ReplaceHttpClient(DoubanApi api, HttpClient replacement)
        {
            var httpClientField = typeof(DoubanApi).GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(httpClientField, "DoubanApi.httpClient 未定义");

            var originalClient = httpClientField!.GetValue(api) as HttpClient;
            Assert.IsNotNull(originalClient, "DoubanApi.httpClient 不是有效的 HttpClient");

            httpClientField.SetValue(api, replacement);
            originalClient!.Dispose();
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

        private sealed class StaticResponseHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode statusCode;
            private readonly string body;

            public StaticResponseHandler(HttpStatusCode statusCode, string body)
            {
                this.statusCode = statusCode;
                this.body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(this.statusCode)
                {
                    Content = new StringContent(this.body),
                });
            }
        }
    }
}
