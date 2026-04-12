using Jellyfin.Plugin.MetaShark.Providers;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeProviderOverviewGuardTest
    {
        [TestMethod]
        public void ShouldRejectOverview_WhenRequestedLanguageIsZhAndOverviewHasNoChinese()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "A reunion episode.", null, null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepOverview_WhenRequestedLanguageIsZhAndOverviewContainsChinese()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "这一集讲述两个年轻人为了一辆车展开较量。", null, null);

            Assert.AreEqual("这一集讲述两个年轻人为了一辆车展开较量。", result.Overview);
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenStrictZhCnOverviewUsesTraditionalChinese()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "這一集講述兩個年輕人為了一輛車展開較量。", null, null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenTraditionalChineseOverviewMatchesParentOverviewUnderStrictZhCn()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "這一集講述兩個年輕人為了一輛車展開較量。",
                "這一集講述兩個年輕人為了一輛車展開較量。",
                null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenTraditionalChineseMissesLegacyBlacklistUnderStrictZhCn()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "这个角色很厲害，也令人驚訝。",
                null,
                null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenTraditionalChinesePreviouslyReliedOnAmbiguousHansEvidence()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "皇后回宮",
                null,
                null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenChineseTextHasNoDistinctHansEvidenceUnderStrictZhCn()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "千里之外",
                null,
                null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepOverview_WhenRequestedLanguageIsNonZh()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("en", "A reunion episode.", null, null);

            Assert.AreEqual("A reunion episode.", result.Overview);
            Assert.AreEqual("en", result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldReturnNullPair_WhenOverviewIsNullOrWhitespace()
        {
            var nullResult = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", null, null, null);
            var whitespaceResult = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "   ", null, null);

            Assert.AreEqual(null, nullResult.Overview);
            Assert.AreEqual(null, nullResult.ResultLanguage);
            Assert.AreEqual(null, whitespaceResult.Overview);
            Assert.AreEqual(null, whitespaceResult.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepMixedOverview_WhenRequestedLanguageIsZhAndOverviewContainsAnyChinese()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh", "第1集 Reunion", null, null);

            Assert.AreEqual("第1集 Reunion", result.Overview);
            Assert.AreEqual("zh", result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenEpisodeOverviewEqualsSeriesOverview()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenEpisodeOverviewEqualsSeasonOverview()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。",
                null,
                "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。");

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenEpisodeOverviewMatchesParentOverviewAfterWhitespaceNormalization()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "世界级的葡萄酒评论家神咲丰多香辞世后，\n留下了一批葡萄酒收藏。",
                "  世界级的葡萄酒评论家神咲丰多香辞世后， 留下了一批葡萄酒收藏。  ",
                null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldRejectOverview_WhenEpisodeOverviewIsHighlySimilarToSeriesOverview()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏！",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                null);

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepOverview_WhenEpisodeOverviewDiffersFromParentOverviews()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence(
                "zh-CN",
                "雫第一次参加神之水滴选拔挑战。",
                "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。");

            Assert.AreEqual("雫第一次参加神之水滴选拔挑战。", result.Overview);
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }
    }
}
