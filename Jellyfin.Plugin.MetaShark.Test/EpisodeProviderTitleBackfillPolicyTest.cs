using Jellyfin.Plugin.MetaShark.Providers;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeProviderTitleBackfillPolicyTest
    {
        [TestMethod]
        public void ShouldBackfillProviderTitle_WhenCanonicalizedLanguageBecomesStrictZhCn()
        {
            var result = EpisodeProvider.ResolveEpisodeTitlePersistence("ZH-cn", "第 1 集", "皇后回宫");

            Assert.AreEqual("皇后回宫", result);
        }

        [DataTestMethod]
        [DataRow("zh")]
        [DataRow("zh-TW")]
        [DataRow("zh-Hans")]
        [DataRow("zh_cn")]
        [DataRow("en")]
        public void ShouldKeepOriginalTitle_WhenLanguageIsNotStrictZhCn(string language)
        {
            var result = EpisodeProvider.ResolveEpisodeTitlePersistence(language, "第 1 集", "皇后回宫");

            Assert.AreEqual("第 1 集", result);
        }

        [TestMethod]
        public void ShouldKeepExistingNonTargetBehavior_WhenOriginalTitleIsNotDefaultJellyfinTitle()
        {
            var result = EpisodeProvider.ResolveEpisodeTitlePersistence("zh-CN", "重逢", "Reunion");

            Assert.AreEqual("Reunion", result);
        }

        [TestMethod]
        public void ShouldKeepOriginalTitle_WhenProviderTitleIsWhitespaceUnderStrictZhCn()
        {
            var result = EpisodeProvider.ResolveEpisodeTitlePersistence("zh-CN", "第 1 集", "   ");

            Assert.AreEqual("第 1 集", result);
        }

        [TestMethod]
        public void ShouldTrimProviderTitleBeforeAcceptingItUnderStrictZhCn()
        {
            var result = EpisodeProvider.ResolveEpisodeTitlePersistence("zh-CN", "第 1 集", "  皇后回宫  ");

            Assert.AreEqual("皇后回宫", result);
        }

        [TestMethod]
        public void ShouldKeepOriginalTitle_WhenTrimmedProviderTitleStillMatchesDefaultJellyfinTitleUnderStrictZhCn()
        {
            var result = EpisodeProvider.ResolveEpisodeTitlePersistence("zh-CN", "第 1 集", "  第 1 集  ");

            Assert.AreEqual("第 1 集", result);
        }

        [DataTestMethod]
        [DataRow("皇后回宮")]
        [DataRow("千里之外")]
        public void ShouldKeepOriginalTitle_WhenProviderTitleFailsStrictZhCnTextPolicy(string providerTitle)
        {
            var result = EpisodeProvider.ResolveEpisodeTitlePersistence("zh-CN", "第 1 集", providerTitle);

            Assert.AreEqual("第 1 集", result);
        }

        [DataTestMethod]
        [DataRow("第 1 集", true)]
        [DataRow("第 12 集", true)]
        [DataRow("第 123 集", true)]
        [DataRow("第01集", false)]
        [DataRow("第1话", false)]
        [DataRow("Episode 1", false)]
        public void ShouldMatchOnlyStrictDefaultJellyfinEpisodeTitleFormat(string title, bool expected)
        {
            var result = EpisodeProvider.IsDefaultJellyfinEpisodeTitle(title);

            Assert.AreEqual(expected, result);
        }
    }
}
