// <copyright file="TvImageSupport.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Model.Entities;

    public static class TvImageSupport
    {
        private static readonly ImageType[] SeriesImages =
        {
            ImageType.Primary,
            ImageType.Backdrop,
            ImageType.Logo,
        };

        private static readonly ImageType[] SeasonImages =
        {
            ImageType.Primary,
        };

        private static readonly ImageType[] EpisodeImages =
        {
            ImageType.Primary,
        };

        public static IReadOnlyList<ImageType> GetSupportedImages(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            return item switch
            {
                Series => SeriesImages,
                Season => SeasonImages,
                Episode => EpisodeImages,
                _ => Array.Empty<ImageType>(),
            };
        }

        public static IEnumerable<ImageType> GetMissingImages(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            return GetSupportedImages(item).Where(imageType => !HasImage(item, imageType));
        }

        private static bool HasImage(BaseItem item, ImageType imageType)
        {
            return imageType switch
            {
                ImageType.Backdrop => HasAnyValidBackdrop(item),
                _ => HasValidImagePath(item.GetImagePath(imageType, 0)),
            };
        }

        private static bool HasAnyValidBackdrop(BaseItem item)
        {
            var index = 0;
            while (true)
            {
                var imagePath = item.GetImagePath(ImageType.Backdrop, index);
                if (string.IsNullOrWhiteSpace(imagePath))
                {
                    return false;
                }

                if (HasValidImagePath(imagePath))
                {
                    return true;
                }

                index++;
            }
        }

        private static bool HasValidImagePath(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return false;
            }

            var candidates = imagePath
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => !string.IsNullOrWhiteSpace(path));

            return candidates.Any(IsValidImageCandidate);
        }

        private static bool IsValidImageCandidate(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                {
                    return File.Exists(uri.LocalPath);
                }

                return true;
            }

            return File.Exists(path);
        }
    }
}
