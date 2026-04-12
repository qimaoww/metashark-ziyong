using Jellyfin.Plugin.MetaShark.Core;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class ChineseLocalePolicyTest
    {
        [TestMethod]
        public void ShouldCanonicalizeMixedCaseZhCn()
        {
            var result = ChineseLocalePolicy.CanonicalizeLanguage("ZH-cn");

            Assert.AreEqual("zh-CN", result);
        }

        [DataTestMethod]
        [DataRow("zh-CN", true)]
        [DataRow("zh", false)]
        [DataRow("zh-TW", false)]
        [DataRow("zh-Hans", false)]
        [DataRow("zh_cn", false)]
        public void ShouldAllowOnlyExactZhCnUnderStrictPolicy(string language, bool expected)
        {
            var result = ChineseLocalePolicy.IsAllowedForStrictZhCn(language);

            Assert.AreEqual(expected, result);
        }

        [DataTestMethod]
        [DataRow("zh-CN", ChineseScriptBucket.Hans)]
        [DataRow("zh-TW", ChineseScriptBucket.Hant)]
        [DataRow("zh", ChineseScriptBucket.Unknown)]
        public void ShouldMapChineseLanguageTagsToExpectedScriptBuckets(string language, ChineseScriptBucket expected)
        {
            var result = ChineseLocalePolicy.GetLanguageScriptBucket(language);

            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        public void ShouldRejectTraditionalTextThatMissedLegacyBlacklistUnderStrictZhCn()
        {
            var result = ChineseLocalePolicy.IsTextAllowedForStrictZhCn("这个角色很厲害，也令人驚訝。");

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void ShouldKeepSimplifiedTextWithNewlyTrackedCharactersUnderStrictZhCn()
        {
            var result = ChineseLocalePolicy.IsTextAllowedForStrictZhCn("这个角色很厉害，也令人惊讶。");

            Assert.IsTrue(result);
        }

        [TestMethod]
        public void ShouldRejectTraditionalTextThatPreviouslyReliedOnAmbiguousHansEvidence()
        {
            var result = ChineseLocalePolicy.IsTextAllowedForStrictZhCn("皇后回宮");

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void ShouldRejectSharedOnlyChineseTextWithoutDistinctHansEvidence()
        {
            var result = ChineseLocalePolicy.IsTextAllowedForStrictZhCn("千里之外");

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void ShouldKeepSimplifiedTextWhenDistinctHansEvidenceExists()
        {
            var result = ChineseLocalePolicy.IsTextAllowedForStrictZhCn("皇后回宫");

            Assert.IsTrue(result);
        }
    }
}
