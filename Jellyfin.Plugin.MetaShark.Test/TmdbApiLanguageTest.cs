using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class TmdbApiLanguageTest
    {
        [TestMethod]
        public void ShouldDifferentiateImageLanguageParamsBetweenZhCnAndZhTw()
        {
            var method = typeof(TmdbApi).GetMethod("GetImageLanguagesParam", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method, "TmdbApi.GetImageLanguagesParam 未定义");

            var zhCn = method.Invoke(null, new object[] { "zh-CN" }) as string;
            var zhTw = method.Invoke(null, new object[] { "zh-TW" }) as string;

            Assert.AreEqual("zh-CN,zh,null,en", zhCn);
            Assert.AreEqual("zh-TW,zh,null,en", zhTw);
            Assert.AreNotEqual(zhCn, zhTw);
        }
    }
}
