using System.Net;
using System.Net.Http;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Model;
using Microsoft.Extensions.Caching.Memory;
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

    public static void SeedSearchResult(DoubanApi api, string keyword, params DoubanSubject[] subjects)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);

        var memoryCacheField = typeof(DoubanApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(memoryCacheField, "DoubanApi.memoryCache 未定义");

        var memoryCache = memoryCacheField!.GetValue(api) as IMemoryCache;
        Assert.IsNotNull(memoryCache, "DoubanApi.memoryCache 不是有效的 IMemoryCache");

        memoryCache!.Set($"search_{keyword}", subjects.ToList(), TimeSpan.FromMinutes(5));
    }

    public static void SeedTvSearchResult(DoubanApi api, string keyword, string sid, string name, int year)
    {
        SeedSearchResult(
            api,
            keyword,
            new DoubanSubject
            {
                Sid = sid,
                Name = name,
                OriginalName = name,
                Category = "电视剧",
                Genre = "电视剧",
                Year = year,
                Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000000.webp",
            });
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
