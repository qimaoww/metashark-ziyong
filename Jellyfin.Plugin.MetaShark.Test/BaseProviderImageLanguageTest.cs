using Jellyfin.Plugin.MetaShark.Providers;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class BaseProviderImageLanguageTest
    {
        [TestMethod]
        public void ShouldKeepGenericZhWhenRequestLanguageIsStrictZhCn()
        {
            var result = BaseProviderProbe.InvokeAdjustImageLanguage("zh", "zh-CN");

            Assert.AreEqual("zh", result);
        }

        [TestMethod]
        public void ShouldStillUpgradeNonChineseGenericLanguageToRequestedRegion()
        {
            var result = BaseProviderProbe.InvokeAdjustImageLanguage("en", "en-US");

            Assert.AreEqual("en-US", result);
        }

        private sealed class BaseProviderProbe : BaseProvider
        {
            public BaseProviderProbe()
                : base(null!, null!, null!, null!, null!, null!, null!, null!)
            {
            }

            public static string InvokeAdjustImageLanguage(string imageLanguage, string requestLanguage)
            {
                return AdjustImageLanguage(imageLanguage, requestLanguage);
            }
        }
    }
}
