using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EnumerableExtensionsTest
    {
        [TestMethod]
        public void FilterManualRemoteImagesByLanguage_ShouldKeepAllowedLanguagesInInputOrder()
        {
            var images = new[]
            {
                CreateImage("en"),
                CreateImage("fr"),
                CreateImage(null),
                CreateImage("ja"),
                CreateImage("ko"),
                CreateImage(string.Empty),
                CreateImage("zh-CN"),
                CreateImage("zh"),
            };

            var filtered = images.FilterManualRemoteImagesByLanguage().ToList();

            CollectionAssert.AreEqual(
                new[] { "en", "<null>", "ja", string.Empty, "zh-CN", "zh" },
                filtered.Select(image => image.Language ?? "<null>").ToArray(),
                "手动 RemoteImages 过滤只应移除非中/日/英/无语言图片，并保持输入相对顺序，不能改成 zh > ja > en > null 排序。");
            Assert.IsFalse(filtered.Any(image => string.Equals(image.Language, "fr", StringComparison.OrdinalIgnoreCase)), "法语图片应被过滤。 ");
            Assert.IsFalse(filtered.Any(image => string.Equals(image.Language, "ko", StringComparison.OrdinalIgnoreCase)), "韩语图片应被过滤。 ");
            Assert.IsTrue(filtered.Any(image => image.Language == null), "null 无语言图片应保留。 ");
            Assert.IsTrue(filtered.Any(image => image.Language == string.Empty), "empty 无语言图片应保留。 ");
        }

        [TestMethod]
        public void ShouldPreferExactZhCnArtworkLanguageOverTraditionalVariants()
        {
            var images = new[]
            {
                CreateImage("zh-TW"),
                CreateImage("zh-Hant"),
                CreateImage("zh-CN"),
            };

            var ordered = images.OrderByLanguageDescending("zh-CN").ToList();

            Assert.AreEqual("zh-CN", ordered[0].Language);
        }

        [TestMethod]
        public void ShouldKeepChineseVariantsDistinctAcrossRequestedLanguagePriority()
        {
            var images = new[]
            {
                CreateImage("zh-TW"),
                CreateImage("zh-CN"),
                CreateImage("zh-Hant"),
            };

            var ordered = images.OrderByLanguageDescending("zh-CN", "zh-TW").ToList();

            Assert.AreEqual("zh-CN", ordered[0].Language);
            Assert.AreEqual("zh-TW", ordered[1].Language);
        }

        private static RemoteImageInfo CreateImage(string? language)
        {
            return new RemoteImageInfo
            {
                Language = language,
                Type = ImageType.Logo,
                CommunityRating = 1,
                VoteCount = 1,
            };
        }
    }
}
