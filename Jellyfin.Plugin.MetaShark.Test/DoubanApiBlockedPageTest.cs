using System.Net;
using System.Net.Http;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.Test;

[TestClass]
public class DoubanApiBlockedPageTest
{
    private static readonly ILoggerFactory TestLoggerFactory = global::Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });

    [TestMethod]
    public void IsBlockedPage_ShouldDetectForbiddenPage()
    {
        var method = typeof(DoubanApi).GetMethod("IsBlockedPage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "DoubanApi.IsBlockedPage 未定义");

        var blockedHtml = "<html><head><title>禁止访问豆瓣</title></head><body>检测到有异常请求从你的 IP 发出</body></html>";
        var normalHtml = "<html><head><title>霸王别姬 (豆瓣)</title></head><body><div id=\"content\"><h1><span>霸王别姬</span></h1></div></body></html>";

        var blocked = (bool)(method.Invoke(null, new object[] { blockedHtml }) ?? false);
        var normal = (bool)(method.Invoke(null, new object[] { normalHtml }) ?? false);

        Assert.IsTrue(blocked, "应识别禁止访问页面");
        Assert.IsFalse(normal, "正常详情页不应被识别为禁止访问");
    }

    [TestMethod]
    public void GetMovieAsync_ShouldReturnNull_WhenBlockedForbiddenResponseReturned()
    {
        var handler = new StaticResponseHandler(BlockedPageHtml, HttpStatusCode.Forbidden);
        using var api = CreateApi(handler);

        var first = api.GetMovieAsync("12345", CancellationToken.None).GetAwaiter().GetResult();
        var second = api.GetMovieAsync("12345", CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsNull(first, "403 被封页面不应返回影视信息");
        Assert.IsNull(second, "403 被封页面不应被缓存为影视信息");
        Assert.AreEqual(2, handler.CallCount, "403 被封页面不应被缓存，第二次调用仍应重新请求并短路");
    }

    [TestMethod]
    public void GetCelebrityAsync_ShouldReturnNull_WhenBlockedPageReturned()
    {
        var handler = new StaticResponseHandler(BlockedPageHtml, HttpStatusCode.Forbidden);
        using var api = CreateApi(handler);

        var first = api.GetCelebrityAsync("12345", CancellationToken.None).GetAwaiter().GetResult();
        var second = api.GetCelebrityAsync("12345", CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsNull(first, "403 被封页面不应返回演员信息");
        Assert.IsNull(second, "403 被封页面不应被缓存为演员信息");
        Assert.AreEqual(2, handler.CallCount, "403 被封页面不应被缓存，第二次调用仍应重新请求并短路");
    }

    [TestMethod]
    public void GetCelebritiesBySidAsync_ShouldReturnEmpty_WhenBlockedPageReturned()
    {
        var handler = new StaticResponseHandler(BlockedPageHtml);
        using var api = CreateApi(handler);

        var first = api.GetCelebritiesBySidAsync("12345", CancellationToken.None).GetAwaiter().GetResult();
        var second = api.GetCelebritiesBySidAsync("12345", CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(0, first.Count, "被封页面不应返回演职员列表");
        Assert.AreEqual(0, second.Count, "被封页面不应被缓存为演职员列表");
        Assert.AreEqual(2, handler.CallCount, "被封页面不应被缓存，第二次调用仍应重新请求并短路");
    }

    [TestMethod]
    public void GetCelebrityPhotosAsync_ShouldReturnEmpty_WhenBlockedPageReturned()
    {
        var handler = new StaticResponseHandler(BlockedPageHtml);
        using var api = CreateApi(handler);

        var first = api.GetCelebrityPhotosAsync("12345", CancellationToken.None).GetAwaiter().GetResult();
        var second = api.GetCelebrityPhotosAsync("12345", CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(0, first.Count, "被封页面不应返回演员照片");
        Assert.AreEqual(0, second.Count, "被封页面不应被缓存为演员照片");
        Assert.AreEqual(2, handler.CallCount, "被封页面不应被缓存，第二次调用仍应重新请求并短路");
    }

    [TestMethod]
    public void GetWallpaperBySidAsync_ShouldReturnEmpty_WhenBlockedPageReturned()
    {
        var handler = new StaticResponseHandler(BlockedPageHtml);
        using var api = CreateApi(handler);

        var first = api.GetWallpaperBySidAsync("12345", CancellationToken.None).GetAwaiter().GetResult();
        var second = api.GetWallpaperBySidAsync("12345", CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(0, first.Count, "被封页面不应返回壁纸");
        Assert.AreEqual(0, second.Count, "被封页面不应被缓存为壁纸");
        Assert.AreEqual(2, handler.CallCount, "被封页面不应被缓存，第二次调用仍应重新请求并短路");
    }

    [TestMethod]
    public void SearchCelebrityAsync_ShouldReturnEmpty_WhenBlockedPageReturned()
    {
        var handler = new StaticResponseHandler(BlockedPageHtml);
        using var api = CreateApi(handler);

        var first = api.SearchCelebrityAsync("受阻演员", CancellationToken.None).GetAwaiter().GetResult();
        var second = api.SearchCelebrityAsync("受阻演员", CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(0, first.Count, "被封页面不应返回搜索结果");
        Assert.AreEqual(0, second.Count, "被封页面不应被缓存为搜索结果");
        Assert.AreEqual(2, handler.CallCount, "被封页面不应被缓存，第二次调用仍应重新请求并短路");
    }

    private static DoubanApi CreateApi(StaticResponseHandler handler)
    {
        var api = new DoubanApi(TestLoggerFactory);
        var httpClientField = typeof(DoubanApi).GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(httpClientField, "DoubanApi.httpClient 未定义");

        var originalClient = (HttpClient)httpClientField!.GetValue(api)!;
        httpClientField.SetValue(api, new HttpClient(handler, disposeHandler: true));
        originalClient.Dispose();

        return api;
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string body;
        private readonly HttpStatusCode statusCode;

        public StaticResponseHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            this.body = body;
            this.statusCode = statusCode;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.CallCount++;
            return Task.FromResult(new HttpResponseMessage(this.statusCode)
            {
                Content = new StringContent(this.body),
            });
        }
    }

    private const string BlockedPageHtml = @"<html>
  <head><title>禁止访问豆瓣</title></head>
  <body>检测到有异常请求从你的 IP 发出
    <div id=""content""><h1 class=""subject-name""><span>真实人物</span></h1></div>
    <div id=""celebrities""><div class=""list-wrapper""><h2>导演 Director</h2><ul class=""celebrities-list""><li class=""celebrity""><div class=""avatar"" style=""background-image:url(https://img1.doubanio.com/view/photo/raw/public/p789.jpg)""></div><div class=""info""><a class=""name"" href=""/celebrity/789/"">真实导演 Real Director</a><span class=""role"">导演 Director</span></div></li></ul></div></div>
    <ul class=""poster-col3""><li data-id=""123""><a href=""/photo/123/""><img src=""https://img1.doubanio.com/view/photo/s/public/p123.jpg"" /></a><div class=""prop"">100x200</div></li></ul>
    <div class=""article""><div class=""result""><div class=""pic""><img src=""https://img1.doubanio.com/view/photo/s/public/p456.jpg"" /></div><h3><a href=""/celebrity/456/"">真实演员 Real Actor</a></h3></div></div>
  </body>
</html>";
}
