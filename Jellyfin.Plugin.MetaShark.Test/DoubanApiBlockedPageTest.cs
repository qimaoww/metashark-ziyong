using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;

namespace Jellyfin.Plugin.MetaShark.Test;

[TestClass]
public class DoubanApiBlockedPageTest
{
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
}
