using Jellyfin.Plugin.MetaShark.Providers;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeProviderOverviewGuardTest
    {
        [TestMethod]
        public void ShouldRejectOverview_WhenRequestedLanguageIsZhAndOverviewHasNoChinese()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "A reunion episode.");

            Assert.AreEqual(null, result.Overview);
            Assert.AreEqual(null, result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepOverview_WhenRequestedLanguageIsZhAndOverviewContainsChinese()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "重逢的一集");

            Assert.AreEqual("重逢的一集", result.Overview);
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepOverview_WhenRequestedLanguageIsNonZh()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("en", "A reunion episode.");

            Assert.AreEqual("A reunion episode.", result.Overview);
            Assert.AreEqual("en", result.ResultLanguage);
        }

        [TestMethod]
        public void ShouldReturnNullPair_WhenOverviewIsNullOrWhitespace()
        {
            var nullResult = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", null);
            var whitespaceResult = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh-CN", "   ");

            Assert.AreEqual(null, nullResult.Overview);
            Assert.AreEqual(null, nullResult.ResultLanguage);
            Assert.AreEqual(null, whitespaceResult.Overview);
            Assert.AreEqual(null, whitespaceResult.ResultLanguage);
        }

        [TestMethod]
        public void ShouldKeepMixedOverview_WhenRequestedLanguageIsZhAndOverviewContainsAnyChinese()
        {
            var result = EpisodeProvider.ResolveEpisodeOverviewPersistence("zh", "第1集 Reunion");

            Assert.AreEqual("第1集 Reunion", result.Overview);
            Assert.AreEqual("zh", result.ResultLanguage);
        }
    }
}
