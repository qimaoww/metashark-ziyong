using Jellyfin.Plugin.MetaShark.Providers;
using System.Reflection;

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

        [TestMethod]
        public void ShouldQueueSearchMissingMetadataTitleBackfill_WhenFeatureEnabledAndResolvedTitleDiffers()
        {
            var result = InvokeShouldQueueSearchMissingMetadataTitleBackfill(true, Guid.NewGuid(), "第 1 集", "  皇后回宫  ");

            Assert.IsTrue(result);
        }

        [TestMethod]
        public void ShouldNotQueueSearchMissingMetadataTitleBackfill_WhenFeatureDisabled()
        {
            var result = InvokeShouldQueueSearchMissingMetadataTitleBackfill(false, Guid.NewGuid(), "第 1 集", "皇后回宫");

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void ShouldNotQueueSearchMissingMetadataTitleBackfill_WhenItemIdIsEmpty()
        {
            var result = InvokeShouldQueueSearchMissingMetadataTitleBackfill(true, Guid.Empty, "第 1 集", "皇后回宫");

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void ShouldNotQueueSearchMissingMetadataTitleBackfill_WhenOriginalTitleIsNotDefaultJellyfinTitle()
        {
            var result = InvokeShouldQueueSearchMissingMetadataTitleBackfill(true, Guid.NewGuid(), "重逢", "皇后回宫");

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void ShouldNotQueueSearchMissingMetadataTitleBackfill_WhenResolvedTitleIsWhitespace()
        {
            var result = InvokeShouldQueueSearchMissingMetadataTitleBackfill(true, Guid.NewGuid(), "第 1 集", "   ");

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void ShouldNotQueueSearchMissingMetadataTitleBackfill_WhenResolvedTitleMatchesOriginalAfterTrim()
        {
            var result = InvokeShouldQueueSearchMissingMetadataTitleBackfill(true, Guid.NewGuid(), "第 1 集", "  第 1 集  ");

            Assert.IsFalse(result);
        }

        private static bool InvokeShouldQueueSearchMissingMetadataTitleBackfill(bool featureEnabled, Guid itemId, string? originalMetadataTitle, string? resolvedTitle)
        {
            var method = typeof(EpisodeProvider).GetMethod(
                "ShouldQueueSearchMissingMetadataTitleBackfill",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.IsNotNull(method, "EpisodeProvider.ShouldQueueSearchMissingMetadataTitleBackfill 未定义");

            return (bool)method!.Invoke(null, new object?[] { featureEnabled, itemId, originalMetadataTitle, resolvedTitle })!;
        }
    }
}
