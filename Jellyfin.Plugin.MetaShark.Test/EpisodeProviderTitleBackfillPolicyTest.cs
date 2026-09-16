using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
using System.Reflection;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeProviderTitleBackfillPolicyTest
    {
        [TestMethod]
        public void ShouldBackfillProviderTitle_WhenCanonicalizedSourceLanguageBecomesStrictZhCn()
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("第 1 集", CreateLocalizedValue("皇后回宫", "ZH-cn"));

            Assert.AreEqual("皇后回宫", result);
        }

        [TestMethod]
        public void ShouldKeepOriginalTitle_WhenOnlyBareZhSourceLanguageExistsWithoutExplicitTitleSourceLanguage()
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("第 1 集", CreateLocalizedValue("皇后回宫", "zh"));

            Assert.AreEqual("第 1 集", result);
        }

        [TestMethod]
        public void ShouldKeepOriginalTitle_WhenTitleSourceLanguageIsMissing()
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("第 1 集", CreateLocalizedValue("皇后回宫", null));

            Assert.AreEqual("第 1 集", result);
        }

        [DataTestMethod]
        [DataRow("zh-TW")]
        [DataRow("zh-Hans")]
        [DataRow("zh_cn")]
        [DataRow("en")]
        public void ShouldKeepOriginalTitle_WhenSourceLanguageIsNotStrictZhCn(string language)
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("第 1 集", CreateLocalizedValue("皇后回宫", language));

            Assert.AreEqual("第 1 集", result);
        }

        [TestMethod]
        public void ShouldKeepExistingNonTargetBehavior_WhenOriginalTitleIsNotDefaultJellyfinTitle()
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("重逢", CreateLocalizedValue("Reunion", "zh-CN"));

            Assert.AreEqual("Reunion", result);
        }

        [TestMethod]
        public void ShouldKeepOriginalTitle_WhenProviderTitleIsWhitespaceUnderTrustedZhCnSource()
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("第 1 集", CreateLocalizedValue("   ", "zh-CN"));

            Assert.AreEqual("第 1 集", result);
        }

        [TestMethod]
        public void ShouldBackfillProviderTitle_WhenSourceLanguageIsZhCnAndTitleUsesTraditionalCharacters()
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("第 1 集", CreateLocalizedValue("皇后回宮", "zh-CN"));

            Assert.AreEqual("皇后回宮", result);
        }

        [TestMethod]
        public void ShouldBackfillProviderTitle_WhenSourceLanguageIsZhCnAndTitleUsesSharedGlyphs()
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("第 1 集", CreateLocalizedValue("千里之外", "zh-CN"));

            Assert.AreEqual("千里之外", result);
        }

        [TestMethod]
        public void ShouldTrimProviderTitleBeforeAcceptingItUnderStrictZhCn()
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("第 1 集", CreateLocalizedValue("  皇后回宫  ", "zh-CN"));

            Assert.AreEqual("皇后回宫", result);
        }

        [TestMethod]
        public void ShouldKeepOriginalTitle_WhenTrimmedProviderTitleStillMatchesDefaultJellyfinTitleUnderStrictZhCn()
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("第 1 集", CreateLocalizedValue("  第 1 集  ", "zh-CN"));

            Assert.AreEqual("第 1 集", result);
        }

        [DataTestMethod]
        [DataRow("第 1 集")]
        [DataRow("Episode 1")]
        public void ShouldKeepOriginalTitle_WhenTrustedZhCnSourceStillReturnsGenericPlaceholderTitle(string providerTitle)
        {
            var result = EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence("第 1 集", CreateLocalizedValue(providerTitle, "zh-CN"));

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
            var result = EpisodeTitleBackfillPolicy.IsDefaultJellyfinEpisodeTitle(title);

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
            var result = EpisodeTitleBackfillPolicy.IsGenericTmdbEpisodeTitle(title);

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
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(false, true, Guid.NewGuid(), "第 1 集", CreateLocalizedValue("皇后回宫", "zh-CN"), "皇后回宫");

            Assert.AreEqual("FeatureDisabled", result);
        }

        [TestMethod]
        public void ShouldReportRequestNotSearchMissingMetadataReason_WhenRequestIsNotSearchMissingMetadata()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, false, Guid.NewGuid(), "第 1 集", CreateLocalizedValue("皇后回宫", "zh-CN"), "皇后回宫");

            Assert.AreEqual("RequestNotSearchMissingMetadata", result);
        }

        [TestMethod]
        public void ShouldReportEpisodeIdMissingReason_WhenSearchMissingMetadataTitleBackfillItemIdIsEmpty()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.Empty, "第 1 集", CreateLocalizedValue("皇后回宫", "zh-CN"), "皇后回宫");

            Assert.AreEqual("EpisodeIdMissing", result);
        }

        [TestMethod]
        public void ShouldReportOriginalTitleNotDefaultReason_WhenOriginalTitleIsNotDefaultJellyfinTitle()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.NewGuid(), "重逢", CreateLocalizedValue("皇后回宫", "zh-CN"), "皇后回宫");

            Assert.AreEqual("OriginalTitleNotDefault", result);
        }

        [TestMethod]
        public void ShouldReportResolvedTitleEmptyReason_WhenResolvedTitleIsWhitespace()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.NewGuid(), "第 1 集", CreateLocalizedValue("   ", "zh-CN"), "   ");

            Assert.AreEqual("ResolvedTitleEmpty", result);
        }

        [TestMethod]
        public void ShouldReportResolvedTitleSameAsOriginalReason_WhenResolvedTitleMatchesOriginalAfterTrim()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.NewGuid(), "第 1 集", CreateLocalizedValue("第 1 集", "zh-TW"), "  第 1 集  ");

            Assert.AreEqual("ResolvedTitleSameAsOriginal", result);
        }

        [TestMethod]
        public void ShouldReportStrictZhCnRejectedReason_WhenProviderTitleSourceLanguageIsNotTrustedZhCn()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.NewGuid(), "第 1 集", CreateLocalizedValue("皇后回宫", "zh"), "第 1 集");

            Assert.AreEqual("StrictZhCnRejected", result);
        }

        [TestMethod]
        public void ShouldReportCandidateQueuedReason_WhenSearchMissingMetadataTitleBackfillCanBeQueued()
        {
            var result = InvokeResolveSearchMissingMetadataTitleBackfillReason(true, true, Guid.NewGuid(), "第 1 集", CreateLocalizedValue("皇后回宫", "zh-CN"), "  皇后回宫  ");

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
            var method = typeof(EpisodeTitleBackfillRefreshClassifier).GetMethod(
                "ShouldQueueSearchMissingMetadataTitleBackfill",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.IsNotNull(method, "EpisodeTitleBackfillRefreshClassifier.ShouldQueueSearchMissingMetadataTitleBackfill 未定义");

            return (bool)method!.Invoke(null, new object?[] { featureEnabled, itemId, originalMetadataTitle, resolvedTitle })!;
        }

        private static string InvokeResolveSearchMissingMetadataTitleBackfillReason(
            bool featureEnabled,
            bool isSearchMissingMetadataRequest,
            Guid itemId,
            string? originalMetadataTitle,
            EpisodeLocalizedValue? providerTitle,
            string? resolvedTitle)
        {
            var method = typeof(EpisodeTitleBackfillRefreshClassifier).GetMethod(
                "ResolveSearchMissingMetadataTitleBackfillReason",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.IsNotNull(method, "EpisodeTitleBackfillRefreshClassifier.ResolveSearchMissingMetadataTitleBackfillReason 未定义");

            return method!.Invoke(null, new object?[] { featureEnabled, isSearchMissingMetadataRequest, itemId, originalMetadataTitle, providerTitle, resolvedTitle })!.ToString()!;
        }

        private static EpisodeLocalizedValue CreateLocalizedValue(string? value, string? sourceLanguage)
        {
            return new EpisodeLocalizedValue
            {
                Value = value,
                SourceLanguage = sourceLanguage,
            };
        }
    }
}
