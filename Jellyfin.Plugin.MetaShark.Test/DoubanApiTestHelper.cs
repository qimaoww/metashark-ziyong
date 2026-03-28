using System.Net;
using System.Net.Http;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.Test;

internal static class DoubanApiTestHelper
{
    public static DoubanApi CreateBlockedDoubanApi(ILoggerFactory loggerFactory)
    {
        var api = new DoubanApi(loggerFactory);
        var httpClientField = typeof(DoubanApi).GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(httpClientField, "DoubanApi.httpClient 未定义");

        var originalClient = (HttpClient)httpClientField!.GetValue(api)!;
        httpClientField.SetValue(api, new HttpClient(new StaticResponseHandler(BlockedPageHtml, HttpStatusCode.Forbidden), disposeHandler: true));
        originalClient.Dispose();

        return api;
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string body;
        private readonly HttpStatusCode statusCode;

        public StaticResponseHandler(string body, HttpStatusCode statusCode)
        {
            this.body = body;
            this.statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(this.statusCode)
            {
                Content = new StringContent(this.body),
            });
        }
    }

    private const string BlockedPageHtml = "<html><head><title>禁止访问豆瓣</title></head><body>检测到有异常请求从你的 IP 发出</body></html>";
}
