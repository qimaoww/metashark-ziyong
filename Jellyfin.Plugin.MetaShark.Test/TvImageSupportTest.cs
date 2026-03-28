using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class TvImageSupportTest
    {
        [TestMethod]
        public void TestGetSupportedSeriesImages()
        {
            var item = new Series();

            var images = TvImageSupport.GetSupportedImages(item).ToArray();

            CollectionAssert.AreEqual(new[]
            {
                ImageType.Primary,
                ImageType.Backdrop,
                ImageType.Logo,
            }, images);
        }

        [TestMethod]
        public void TestGetSupportedSeasonImages()
        {
            var item = new Season();

            var images = TvImageSupport.GetSupportedImages(item).ToArray();

            CollectionAssert.AreEqual(new[]
            {
                ImageType.Primary,
            }, images);
        }

        [TestMethod]
        public void TestGetSupportedEpisodeImages()
        {
            var item = new Episode();

            var images = TvImageSupport.GetSupportedImages(item).ToArray();

            CollectionAssert.AreEqual(new[]
            {
                ImageType.Primary,
            }, images);
        }

        [TestMethod]
        public void TestGetMissingSeriesImagesWhenAllAreMissing()
        {
            var item = new Series();

            var images = TvImageSupport.GetMissingImages(item).ToArray();

            CollectionAssert.AreEqual(new[]
            {
                ImageType.Primary,
                ImageType.Backdrop,
                ImageType.Logo,
            }, images);
        }

        [TestMethod]
        public void TestGetMissingSeriesImagesWhenSomeAlreadyExist()
        {
            var item = new Series();
            SetImages(item, ImageType.Primary, ImageType.Logo);

            var images = TvImageSupport.GetMissingImages(item).ToArray();

            CollectionAssert.AreEqual(new[]
            {
                ImageType.Backdrop,
            }, images);
        }

        [TestMethod]
        public void TestGetMissingSeasonImagesWhenPrimaryAlreadyExists()
        {
            var item = new Season();
            SetImages(item, ImageType.Primary);

            var images = TvImageSupport.GetMissingImages(item).ToArray();

            CollectionAssert.AreEqual(Array.Empty<ImageType>(), images);
        }

        [TestMethod]
        public void TestGetMissingEpisodeImagesWhenPrimaryAlreadyExists()
        {
            var item = new Episode();
            SetImages(item, ImageType.Primary);

            var images = TvImageSupport.GetMissingImages(item).ToArray();

            CollectionAssert.AreEqual(Array.Empty<ImageType>(), images);
        }

        [TestMethod]
        public void TestGetMissingSeasonImagesWhenLocalPrimaryFileDeleted()
        {
            var item = new Season();
            var filePath = Path.Combine(Path.GetTempPath(), $"metashark-tv-image-{Guid.NewGuid():N}.jpg");
            File.WriteAllText(filePath, "test");
            item.DateCreated = DateTime.UtcNow;
            item.ImageInfos = new[]
            {
                new ItemImageInfo
                {
                    Type = ImageType.Primary,
                    Path = filePath,
                },
            };

            var beforeDelete = TvImageSupport.GetMissingImages(item).ToArray();
            CollectionAssert.AreEqual(Array.Empty<ImageType>(), beforeDelete);

            File.Delete(filePath);

            var images = TvImageSupport.GetMissingImages(item).ToArray();

            CollectionAssert.AreEqual(new[]
            {
                ImageType.Primary,
            }, images);
        }

        private static void SetImages(BaseItem item, params ImageType[] imageTypes)
        {
            foreach (var imageType in imageTypes)
            {
                item.SetImagePath(imageType, $"https://example.com/images/{imageType}.jpg");
            }
        }
    }
}
