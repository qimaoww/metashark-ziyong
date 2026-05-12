// <copyright file="TmdbCorrectionRefreshIntentClassifier.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using Microsoft.AspNetCore.Http;

    internal static class TmdbCorrectionRefreshIntentClassifier
    {
        public static bool IsExplicitSearchMissingMetadataRefresh(HttpContext? httpContext)
        {
            return TryResolveExplicitSearchMissingMetadataRefreshItemId(httpContext, out _);
        }

        public static bool TryResolveExplicitSearchMissingMetadataRefreshItemId(HttpContext? httpContext, out Guid itemId)
        {
            itemId = Guid.Empty;
            var request = httpContext?.Request;
            if (request == null || !HttpMethods.IsPost(request.Method) || !TryResolveRefreshItemId(request.Path.Value, out itemId))
            {
                itemId = Guid.Empty;
                return false;
            }

            return HasQueryValue(request, "metadataRefreshMode", "FullRefresh")
                && HasQueryValue(request, "replaceAllMetadata", "false", "ReplaceAllMetadata");
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

        private static bool HasQueryValue(HttpRequest request, string key, params string[] expectedValues)
        {
            if (!request.Query.TryGetValue(key, out var values))
            {
                return false;
            }

            foreach (var value in values)
            {
                foreach (var expectedValue in expectedValues)
                {
                    if (string.Equals(value?.Trim(), expectedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
