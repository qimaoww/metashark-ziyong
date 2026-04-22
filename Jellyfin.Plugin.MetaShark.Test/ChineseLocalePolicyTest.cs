using Jellyfin.Plugin.MetaShark.Core;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class ChineseLocalePolicyTest
    {
        [DataTestMethod]
        [DataRow("ZH-cn", "zh-CN")]
        [DataRow(" zh-hant ", "zh-Hant")]
        [DataRow("zh-hk", "zh-HK")]
        public void ShouldCanonicalizeChineseLanguageTags(string language, string expected)
        {
            var result = ChineseLocalePolicy.CanonicalizeLanguage(language);

            Assert.AreEqual(expected, result);
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
        [DataRow("zh-Hans", ChineseScriptBucket.Hans)]
        [DataRow("zh-TW", ChineseScriptBucket.Hant)]
        [DataRow("zh-HK", ChineseScriptBucket.Hant)]
        [DataRow("zh-MO", ChineseScriptBucket.Hant)]
        [DataRow("zh-Hant", ChineseScriptBucket.Hant)]
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

        [TestMethod]
        public void ShouldPreferExactZhCnPeopleLocalization()
        {
            var result = ChineseLocalePolicy.TryGetPreferredPeopleLocalization(
                new[]
                {
                    CreateLocalizedValue("zh", "通用中文名"),
                    CreateLocalizedValue("zh-Hant", "繁體中文名"),
            CreateLocalizedValue("zh-CN", "中文名"),
                },
                localizedValue => localizedValue.Language,
                localizedValue => localizedValue.Value,
                "Fallback Name",
                out var value,
                out var sourceLanguage);

            Assert.IsTrue(result);
            Assert.AreEqual("中文名", value);
            Assert.AreEqual("zh-CN", sourceLanguage);
        }

        [TestMethod]
        public void ShouldIgnoreNonExactZhCnPeopleLocalization()
        {
            var result = ChineseLocalePolicy.TryGetPreferredPeopleLocalization(
                new[]
                {
                    CreateLocalizedValue("zh", "通用中文名"),
                    CreateLocalizedValue("zh-Hant", "繁體中文名"),
                    CreateLocalizedValue("zh-Hans", "简体中文名"),
                },
                localizedValue => localizedValue.Language,
                localizedValue => localizedValue.Value,
                "Fallback Name",
                out var value,
                out var sourceLanguage);

            Assert.IsFalse(result);
            Assert.IsNull(value);
            Assert.IsNull(sourceLanguage);
        }

        [TestMethod]
        public void ShouldNotUseExplicitFallbackWhenExactZhCnPeopleLocalizationIsBlank()
        {
            var result = ChineseLocalePolicy.TryGetPreferredPeopleLocalization(
                new[]
                {
                    CreateLocalizedValue("zh-CN", "   "),
                    CreateLocalizedValue("zh-CN", null),
                    CreateLocalizedValue("zh", "可忽略的通用中文名"),
                },
                localizedValue => localizedValue.Language,
                localizedValue => localizedValue.Value,
                "Fallback Name",
                out var value,
                out var sourceLanguage);

            Assert.IsFalse(result);
            Assert.IsNull(value);
            Assert.IsNull(sourceLanguage);
        }

        [TestMethod]
        public void ShouldRejectBlankFallbackWhenNoUsablePeopleLocalizationExists()
        {
            var result = ChineseLocalePolicy.TryGetPreferredPeopleLocalization(
                new[]
                {
                    CreateLocalizedValue("zh-CN", "   "),
                    CreateLocalizedValue("zh", null),
                },
                localizedValue => localizedValue.Language,
                localizedValue => localizedValue.Value,
                "  ",
                out var value,
                out var sourceLanguage);

            Assert.IsFalse(result);
            Assert.IsNull(value);
            Assert.IsNull(sourceLanguage);
        }

        private static LocalizedValue CreateLocalizedValue(string? language, string? value)
        {
            return new LocalizedValue
            {
                Language = language,
                Value = value,
            };
        }

        private sealed class LocalizedValue
        {
            public string? Language { get; init; }

            public string? Value { get; init; }
        }
    }
}
