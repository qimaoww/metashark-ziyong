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

        [DataTestMethod]
        [DataRow("zh-CN", "TW", "zh-HK", "zh-CN")]
        [DataRow("zh-SG", "TW", "zh-HK", "zh-SG")]
        [DataRow("zh-TW", "CN", "zh-CN", "zh-TW")]
        [DataRow("zh-HK", "CN", "zh-CN", "zh-HK")]
        [DataRow("zh-MO", null, "zh-CN", "zh-HK")]
        [DataRow("zh-Hans", "SG", "zh-TW", "zh-SG")]
        [DataRow("zh-Hans", "TW", "zh-TW", "zh-CN")]
        [DataRow("zh-Hant", "HK", "zh-CN", "zh-HK")]
        [DataRow("zh-Hant", "MO", "zh-CN", "zh-HK")]
        [DataRow("zh-Hant", "CN", "zh-CN", "zh-TW")]
        [DataRow("zh", "CN", "zh-TW", "zh-CN")]
        [DataRow("zh", "SG", "zh-TW", "zh-SG")]
        [DataRow("zh", "TW", "zh-CN", "zh-TW")]
        [DataRow("zh", "HK", "zh-CN", "zh-HK")]
        [DataRow("zh", "MO", "zh-CN", "zh-HK")]
        [DataRow("zh", "AU", "zh-TW", "zh-TW")]
        [DataRow("zh", null, "zh-HK", "zh-HK")]
        [DataRow("en-us", "CN", "zh-CN", "en-US")]
        public void ShouldResolveTmdbMetadataLanguage(string language, string? countryCode, string defaultLocale, string expected)
        {
            var result = ChineseLocalePolicy.ResolveTmdbMetadataLanguage(language, countryCode, defaultLocale);

            Assert.AreEqual(expected, result);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("invalid")]
        [DataRow("zh-Hans")]
        public void ShouldNormalizeInvalidDefaultChineseLocaleToZhCn(string? value)
        {
            Assert.AreEqual("zh-CN", ChineseLocalePolicy.NormalizeDefaultChineseMetadataLocale(value));
        }

        [DataTestMethod]
        [DataRow("zh-CN", "CN")]
        [DataRow("zh-SG", "SG")]
        [DataRow("zh-TW", "TW")]
        [DataRow("zh-HK", "HK")]
        [DataRow("zh", null)]
        [DataRow("en-US", null)]
        public void ShouldMapResolvedChineseLocaleToTmdbRegion(string language, string? expected)
        {
            Assert.AreEqual(expected, ChineseLocalePolicy.GetTmdbChineseRegionCode(language));
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
