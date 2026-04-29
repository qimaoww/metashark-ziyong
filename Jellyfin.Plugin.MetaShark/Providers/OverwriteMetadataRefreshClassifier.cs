// <copyright file="OverwriteMetadataRefreshClassifier.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using Microsoft.AspNetCore.Http;

    internal static class OverwriteMetadataRefreshClassifier
    {
        public static bool IsOverwriteMetadataRefresh(HttpContext? httpContext, Guid? expectedItemId = null)
        {
            var request = httpContext?.Request;
            if (request == null || !HttpMethods.IsPost(request.Method))
            {
                return false;
            }

            if (!TryResolveRefreshItemId(request.Path.Value, out var requestItemId))
            {
                return false;
            }

            if (expectedItemId.HasValue && requestItemId != expectedItemId.Value)
            {
                return false;
            }

            return HasTrueReplaceAllMetadata(request);
        }

        private static bool TryResolveRefreshItemId(string? path, out Guid itemId)
        {
            itemId = Guid.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 3
                && string.Equals(segments[0], "Items", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[1], out itemId)
                && string.Equals(segments[2], "Refresh", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasTrueReplaceAllMetadata(HttpRequest request)
        {
            return HasTrueQueryValue(request, "replaceAllMetadata")
                || HasTrueQueryValue(request, "ReplaceAllMetadata");
        }

        private static bool HasTrueQueryValue(HttpRequest request, string key)
        {
            if (!request.Query.TryGetValue(key, out var values))
            {
                return false;
            }

            foreach (var value in values)
            {
                if (bool.TryParse(value, out var replaceAllMetadata) && replaceAllMetadata)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
