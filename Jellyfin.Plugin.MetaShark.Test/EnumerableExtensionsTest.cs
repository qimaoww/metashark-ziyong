using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EnumerableExtensionsTest
    {
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

        private static RemoteImageInfo CreateImage(string language)
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
