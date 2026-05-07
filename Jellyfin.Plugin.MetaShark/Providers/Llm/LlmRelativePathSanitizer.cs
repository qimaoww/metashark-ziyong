// <copyright file="LlmRelativePathSanitizer.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public sealed class LlmRelativePathSanitizer
    {
        private const int FallbackSegmentCount = 3;

        public static string Sanitize(string? itemPath, IEnumerable<string?> libraryRoots, string? mediaType)
        {
            var parsedPath = ParsePath(itemPath);
            var pathSegments = parsedPath.Segments;
            if (pathSegments.Count == 0)
            {
                return string.Empty;
            }

            var matchedRelativeSegments = TryGetRelativeSegmentsFromLibraryRoot(pathSegments, libraryRoots);
            var safeSegments = matchedRelativeSegments ?? GetFallbackSegments(parsedPath);
            return string.Join("/", safeSegments.Where(IsAllowedSegment));
        }

        private static List<string>? TryGetRelativeSegmentsFromLibraryRoot(List<string> pathSegments, IEnumerable<string?> libraryRoots)
        {
            foreach (var root in libraryRoots ?? Array.Empty<string?>())
            {
                var rootSegments = ParsePath(root).Segments;
                if (rootSegments.Count == 0 || rootSegments.Count >= pathSegments.Count)
                {
                    continue;
                }

                if (StartsWithSegments(pathSegments, rootSegments))
                {
                    return pathSegments.Skip(rootSegments.Count).ToList();
                }
            }

            return null;
        }

        private static List<string> GetFallbackSegments(ParsedPath parsedPath)
        {
            var pathSegments = parsedPath.Segments.Skip(parsedPath.FallbackStartIndex);
            var filteredSegments = pathSegments.Where(IsAllowedSegment).ToList();
            var anchorIndex = filteredSegments.FindLastIndex(IsLikelyLibraryCategorySegment);
            if (anchorIndex >= 0)
            {
                return filteredSegments.Skip(anchorIndex).ToList();
            }

            var takeCount = Math.Min(FallbackSegmentCount, filteredSegments.Count);
            return filteredSegments.Skip(filteredSegments.Count - takeCount).ToList();
        }

        private static bool StartsWithSegments(List<string> pathSegments, List<string> rootSegments)
        {
            for (var index = 0; index < rootSegments.Count; index++)
            {
                if (!string.Equals(pathSegments[index], rootSegments[index], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static ParsedPath ParsePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new ParsedPath(new List<string>(), 0);
            }

            var normalizedPath = path.Trim().Replace('\\', '/');
            var segments = normalizedPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(segment => segment.Length > 0 && segment != "." && segment != "..")
                .ToList();
            var fallbackStartIndex = normalizedPath.StartsWith("//", StringComparison.Ordinal) && segments.Count >= 2 ? 2 : 0;
            return new ParsedPath(segments, fallbackStartIndex);
        }

        private static bool IsAllowedSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment == "." || segment == "..")
            {
                return false;
            }

            if (segment[0] == '.')
            {
                return false;
            }

            if (segment[^1] == ':')
            {
                return false;
            }

            return !IsForbiddenRootSegment(segment);
        }

        private static bool IsForbiddenRootSegment(string segment)
        {
            return string.Equals(segment, "root", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "home", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "mnt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "opt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "media", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyLibraryCategorySegment(string segment)
        {
            return string.Equals(segment, "Movies", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "Movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "Films", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "TV", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "Shows", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "Series", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "Anime", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ParsedPath
        {
            public ParsedPath(List<string> segments, int fallbackStartIndex)
            {
                this.Segments = segments;
                this.FallbackStartIndex = fallbackStartIndex;
            }

            public List<string> Segments { get; }

            public int FallbackStartIndex { get; }
        }
    }
}
