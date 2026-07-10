using Jellyfin.Plugin.MetaShark.Core;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class TmdbTitleFallbackPolicyTest
    {
        [DataTestMethod]
        [DataRow("zh-CN")]
        [DataRow("zh-SG")]
        [DataRow("zh-TW")]
        [DataRow("zh-HK")]
        public void ShouldQueryMatchingRegionWhenDetailsFellBackToOriginal(string language)
        {
            Assert.IsTrue(TmdbTitleFallbackPolicy.ShouldTryRegionalAlternativeTitle(
                language,
                "チェンソーマン",
                "チェンソーマン",
                "ja"));
        }

        [TestMethod]
        public void ShouldNotQueryWhenLocalizedTitleAlreadyExists()
        {
            Assert.IsFalse(TmdbTitleFallbackPolicy.ShouldTryRegionalAlternativeTitle(
                "zh-TW",
                "鏈鋸人",
                "チェンソーマン",
                "ja"));
        }

        [TestMethod]
        public void ShouldNotQueryForNonRegionalOrOriginalChineseTitle()
        {
            Assert.IsFalse(TmdbTitleFallbackPolicy.ShouldTryRegionalAlternativeTitle("en-US", "Title", "Title", "en"));
            Assert.IsFalse(TmdbTitleFallbackPolicy.ShouldTryRegionalAlternativeTitle("zh-CN", "三体", "三体", "zh"));
        }

        [TestMethod]
        public void ResolveTitlePrefersRegionalAlternativeWithoutChangingOriginalFallbackOrder()
        {
            Assert.AreEqual("电锯人", TmdbTitleFallbackPolicy.ResolveTitle("チェンソーマン", "チェンソーマン", " 电锯人 "));
            Assert.AreEqual("チェンソーマン", TmdbTitleFallbackPolicy.ResolveTitle(" チェンソーマン ", "Original", null));
            Assert.AreEqual("Original", TmdbTitleFallbackPolicy.ResolveTitle(null, " Original ", null));
        }
    }
}
