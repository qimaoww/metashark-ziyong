using Jellyfin.Plugin.MetaShark.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.MetaShark.Test;

/// <summary>
/// Jellyfin 12 的语言下拉使用显示名（Chinese / Chinese (Traditional) 等），
/// 库、服务器或条目的 PreferredMetadataLanguage 可能直接存这些值，需要先归一化。
/// </summary>
[TestClass]
public class ChineseLanguageAliasTest
{
    [DataTestMethod]
    [TestCategory("Stable")]
    [DataRow("Chinese", "zh")]
    [DataRow("chinese", "zh")]
    [DataRow("Chinese (Simplified)", "zh-CN")]
    [DataRow("Chinese (China)", "zh-CN")]
    [DataRow("Chinese (Traditional)", "zh-TW")]
    [DataRow("Chinese (Taiwan)", "zh-TW")]
    [DataRow("Chinese (Hong Kong)", "zh-HK")]
    [DataRow("Chinese (Singapore)", "zh-SG")]
    [DataRow("Chinese (Macao)", "zh-MO")]
    [DataRow("Chinese (Bilingual)", "zh")]
    [DataRow("zho", "zh")]
    [DataRow("chi", "zh")]
    [DataRow("ze", "zh")]
    [DataRow("Chinese (Classical)", "zh")]
    [DataRow("Chinese (Simplified, China)", "zh-CN")]
    [DataRow("Chinese (Traditional, Taiwan)", "zh-TW")]
    [DataRow("Chinese (Traditional, Hong Kong)", "zh-HK")]
    public void ShouldCanonicalizeJellyfinChineseDisplayNames(string language, string expected)
    {
        Assert.AreEqual(expected, ChineseLocalePolicy.CanonicalizeLanguage(language));
    }

    [DataTestMethod]
    [TestCategory("Stable")]
    [DataRow("Chinese", true)]
    [DataRow("Chinese (Traditional)", true)]
    [DataRow("Chinese (Bilingual)", true)]
    [DataRow("zho", true)]
    [DataRow("ze", true)]
    [DataRow("Chinese (Classical)", true)]
    [DataRow("English", false)]
    [DataRow("Japanese", false)]
    public void ShouldDetectChineseRequestsFromDisplayNames(string language, bool expected)
    {
        Assert.AreEqual(expected, ChineseLocalePolicy.IsChineseRequest(language));
    }

    [DataTestMethod]
    [TestCategory("Stable")]
    [DataRow("Chinese", "CN", "zh-CN")]
    [DataRow("Chinese", "TW", "zh-TW")]
    [DataRow("Chinese", "HK", "zh-HK")]
    [DataRow("Chinese (Simplified)", "TW", "zh-CN")]
    [DataRow("Chinese (Traditional)", "CN", "zh-TW")]
    [DataRow("Chinese (Hong Kong)", "CN", "zh-HK")]
    [DataRow("Chinese (Bilingual)", "CN", "zh-CN")]
    public void ShouldResolveTmdbLocaleFromDisplayNames(string language, string country, string expected)
    {
        Assert.AreEqual(expected, ChineseLocalePolicy.ResolveTmdbMetadataLanguage(language, country, "zh-CN"));
    }

    [DataTestMethod]
    [TestCategory("Stable")]
    [DataRow("Chinese (Traditional)", "zh-TW")]
    [DataRow("Chinese (Hong Kong)", "zh-HK")]
    [DataRow("Chinese (Bilingual)", "zh-CN")]
    [DataRow("Chinese", "zh-CN")]
    public void ShouldNormalizeDefaultChineseLocaleFromDisplayNames(string language, string expected)
    {
        Assert.AreEqual(expected, ChineseLocalePolicy.NormalizeDefaultChineseMetadataLocale(language));
    }

    [DataTestMethod]
    [TestCategory("Stable")]
    [DataRow("Chinese (Traditional)", ChineseScriptBucket.Hant)]
    [DataRow("Chinese (Hong Kong)", ChineseScriptBucket.Hant)]
    [DataRow("Chinese (Simplified)", ChineseScriptBucket.Hans)]
    public void ShouldResolveScriptBucketFromDisplayNames(string language, ChineseScriptBucket expected)
    {
        Assert.AreEqual(expected, ChineseLocalePolicy.GetLanguageScriptBucket(language));
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void DisplayNamesShouldParticipateInStrictZhCnPolicy()
    {
        // 简体显示名等价于 zh-CN，繁体显示名不能通过严格简中校验。
        Assert.IsTrue(ChineseLocalePolicy.IsAllowedForStrictZhCn("Chinese (Simplified)"));
        Assert.IsFalse(ChineseLocalePolicy.IsAllowedForStrictZhCn("Chinese (Traditional)"));
        Assert.IsFalse(ChineseLocalePolicy.IsAllowedForStrictZhCn("Chinese"));
    }
}
