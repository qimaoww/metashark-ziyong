using System.Collections.Generic;
using Jellyfin.Plugin.MetaShark.Providers.Llm;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmRelativePathSanitizerTest
    {
        [TestMethod]
        public void Sanitize_RemovesMatchingLinuxLibraryRoot()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                "/mnt/media/Movies/Inception (2010)/Inception.mkv",
                new[] { "/mnt/media" },
                "Movie");

            Assert.AreEqual("Movies/Inception (2010)/Inception.mkv", result);
        }

        [TestMethod]
        public void Sanitize_RemovesMatchingWindowsLibraryRootAndUsesForwardSlash()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                @"C:\Media\TV\三体\Season 01\S01E01.mkv",
                new[] { @"C:\Media" },
                "Episode");

            Assert.AreEqual("TV/三体/Season 01/S01E01.mkv", result);
        }

        [TestMethod]
        public void Sanitize_RemovesMatchingUncLibraryRootWithoutServerOrShare()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                @"\\NAS\share\Movies\红辣椒 (2006)\Paprika.mkv",
                new[] { @"\\NAS\share" },
                "Movie");

            Assert.AreEqual("Movies/红辣椒 (2006)/Paprika.mkv", result);
        }

        [TestMethod]
        public void Sanitize_RemovesGenericUnmatchedUncAuthorityWithArbitraryNames()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                @"\\SECRET-SERVER\PRIVATE-SHARE\Project\Show\S01E01.mkv",
                Array.Empty<string?>(),
                "Episode");

            Assert.AreEqual("Project/Show/S01E01.mkv", result);
            AssertSafeRelativePath(result);
            Assert.IsFalse(result.Contains("SECRET-SERVER", System.StringComparison.Ordinal), result);
            Assert.IsFalse(result.Contains("PRIVATE-SHARE", System.StringComparison.Ordinal), result);
        }

        [TestMethod]
        public void Sanitize_RemovesGenericUnmatchedUncAuthorityWithoutCategoryAnchor()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                @"\\SERVER42\Share99\A\B\C.mkv",
                Array.Empty<string?>(),
                "Movie");

            Assert.AreEqual("A/B/C.mkv", result);
            AssertSafeRelativePath(result);
            Assert.IsFalse(result.Contains("SERVER42", System.StringComparison.Ordinal), result);
            Assert.IsFalse(result.Contains("Share99", System.StringComparison.Ordinal), result);
        }

        [TestMethod]
        public void Sanitize_RemovesTwoSegmentUnmatchedUncAuthorityAndShare()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                @"\\SERVER42\Share99",
                Array.Empty<string?>(),
                "Movie");

            Assert.AreEqual(string.Empty, result);
            Assert.IsFalse(result.Contains("SERVER42", System.StringComparison.Ordinal), result);
            Assert.IsFalse(result.Contains("Share99", System.StringComparison.Ordinal), result);
        }

        [TestMethod]
        public void Sanitize_PreservesMatchedGenericUncLibraryRootRelativePath()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                @"\\AnyServer\AnyShare\Movies\Film\Film.mkv",
                new[] { @"\\AnyServer\AnyShare" },
                "Movie");

            Assert.AreEqual("Movies/Film/Film.mkv", result);
            AssertSafeRelativePath(result);
            Assert.IsFalse(result.Contains("AnyServer", System.StringComparison.Ordinal), result);
            Assert.IsFalse(result.Contains("AnyShare", System.StringComparison.Ordinal), result);
        }

        [TestMethod]
        public void Sanitize_ReturnsEmptyForEmptyPath()
        {
            var result = LlmRelativePathSanitizer.Sanitize(string.Empty, new[] { "/mnt/media" }, "Movie");

            Assert.AreEqual(string.Empty, result);
        }

        [TestMethod]
        public void Sanitize_WhenRootDoesNotMatchKeepsOnlyTrailingSegments()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                "/opt/jellyfin/data/Movies/Inception (2010)/Inception.mkv",
                new[] { "/mnt/media" },
                "Movie");

            Assert.AreEqual("Movies/Inception (2010)/Inception.mkv", result);
            AssertSafeRelativePath(result);
        }

        [TestMethod]
        public void Sanitize_RemovesParentTraversalAndEmptySegments()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                "/mnt/media//Movies/../Secrets//Movie.mkv",
                new[] { "/mnt/media" },
                "Movie");

            Assert.AreEqual("Movies/Secrets/Movie.mkv", result);
            AssertSafeRelativePath(result);
        }

        [TestMethod]
        public void Sanitize_DoesNotExposeHiddenAbsoluteAncestors()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                "/home/test/.hidden/Series/Show/S01E01.mkv",
                new[] { "/mnt/media" },
                "Episode");

            Assert.AreEqual("Series/Show/S01E01.mkv", result);
            AssertSafeRelativePath(result);
            Assert.IsFalse(result.Contains(".hidden", System.StringComparison.Ordinal), result);
        }

        [TestMethod]
        public void Sanitize_FolderMediaPathKeepsFolderRelativePath()
        {
            var result = LlmRelativePathSanitizer.Sanitize(
                "/media/Shows/Some Show/Season 01",
                new[] { "/media" },
                "Season");

            Assert.AreEqual("Shows/Some Show/Season 01", result);
        }

        private static void AssertSafeRelativePath(string value)
        {
            var forbiddenFragments = new List<string>
            {
                "C:",
                "\\\\NAS",
                "/root",
                "/home",
                "/mnt",
                "/opt",
                "/media",
                "..",
                "//",
            };

            foreach (var fragment in forbiddenFragments)
            {
                Assert.IsFalse(value.Contains(fragment, System.StringComparison.Ordinal), value);
            }
        }
    }
}
