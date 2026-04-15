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

        [TestMethod]
        public void ShouldBackfillProviderTitle_WhenBareZhLanguageAndProviderTitlePassesStrictZhCnTextPolicy()
        {
            var result = EpisodeProvider.ResolveEpisodeTitlePersistence("zh", "第 1 集", "皇后回宫");

            Assert.AreEqual("皇后回宫", result);
        }

        [DataTestMethod]
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
        [DataRow("第 1 集")]
        [DataRow("   ")]
        [DataRow("皇后回宮")]
        [DataRow("千里之外")]
        public void ShouldKeepOriginalTitle_WhenBareZhLanguageButProviderTitleFailsStrictZhCnTextPolicy(string providerTitle)
        {
            var result = EpisodeProvider.ResolveEpisodeTitlePersistence("zh", "第 1 集", providerTitle);

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

        [DataTestMethod]
        [DataRow("第 1 集", true)]
        [DataRow("Episode 1", true)]
        [DataRow("  episode 12  ", true)]
        [DataRow("第01集", false)]
        [DataRow("Episode One", false)]
        [DataRow("皇后回宫", false)]
        public void ShouldMatchOnlyGenericTmdbEpisodeTitleFormats(string title, bool expected)
        {
            var method = typeof(EpisodeProvider).GetMethod(
                "IsGenericTmdbEpisodeTitle",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.IsNotNull(method, "EpisodeProvider.IsGenericTmdbEpisodeTitle 未定义");

            var result = (bool)method!.Invoke(null, new object?[] { title })!;

            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        public void ShouldQueueSearchMissingMetadataTitleBackfill_WhenFeatureEnabledAndResolvedTitleDiffers()
        {
            var result = InvokeShouldQueueSearchMissingMetadataTitleBackfill(true, Guid.NewGuid(), "第 1 集", "  皇后回宫  ");

            Assert.IsTrue(result);
        }

        [TestMethod]
        public void ShouldReportFeatureDisabledReason_WhenSearchMissingMetadataTitleBackfillFeatureIsOff()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(false, true, Guid.NewGuid(), "zh-CN", "第 1 集", "皇后回宫", "皇后回宫");

            Assert.AreEqual("FeatureDisabled", result);
        }

        [TestMethod]
        public void ShouldReportEpisodeIdMissingReason_WhenSearchMissingMetadataTitleBackfillItemIdIsEmpty()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.Empty, "zh-CN", "第 1 集", "皇后回宫", "皇后回宫");

            Assert.AreEqual("EpisodeIdMissing", result);
        }

        [TestMethod]
        public void ShouldReportOriginalTitleNotDefaultReason_WhenOriginalTitleIsNotDefaultJellyfinTitle()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.NewGuid(), "zh-CN", "重逢", "皇后回宫", "皇后回宫");

            Assert.AreEqual("OriginalTitleNotDefault", result);
        }

        [TestMethod]
        public void ShouldReportResolvedTitleEmptyReason_WhenResolvedTitleIsWhitespace()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.NewGuid(), "zh-CN", "第 1 集", "   ", "   ");

            Assert.AreEqual("ResolvedTitleEmpty", result);
        }

        [TestMethod]
        public void ShouldReportResolvedTitleSameAsOriginalReason_WhenResolvedTitleMatchesOriginalAfterTrim()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.NewGuid(), "zh-TW", "第 1 集", "第 1 集", "  第 1 集  ");

            Assert.AreEqual("ResolvedTitleSameAsOriginal", result);
        }

        [TestMethod]
        public void ShouldReportStrictZhCnRejectedReason_WhenProviderTitleFailsStrictZhCnTextPolicy()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.NewGuid(), "zh-CN", "第 1 集", "皇后回宮", "第 1 集");

            Assert.AreEqual("StrictZhCnRejected", result);
        }

        [TestMethod]
        public void ShouldReportCandidateQueuedReason_WhenSearchMissingMetadataTitleBackfillCanBeQueued()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.NewGuid(), "zh-CN", "第 1 集", "皇后回宫", "  皇后回宫  ");

            Assert.AreEqual("CandidateQueued", result);
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

        private static string InvokeResolveSearchMissingMetadataTitleBackfillReason(
            bool featureEnabled,
            bool isSearchMissingMetadataRequest,
            Guid itemId,
            string? metadataLanguage,
            string? originalMetadataTitle,
            string? providerTitle,
            string? resolvedTitle)
        {
            var method = typeof(EpisodeProvider).GetMethod(
                "ResolveSearchMissingMetadataTitleBackfillReason",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.IsNotNull(method, "EpisodeProvider.ResolveSearchMissingMetadataTitleBackfillReason 未定义");

            return method!.Invoke(null, new object?[] { featureEnabled, isSearchMissingMetadataRequest, itemId, metadataLanguage, originalMetadataTitle, providerTitle, resolvedTitle })!.ToString()!;
        }
    }
}
