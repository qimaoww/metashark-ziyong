using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmPromptContextBuilderTest
    {
        private static readonly HashSet<string> AllowedFields = new HashSet<string>
        {
            "MediaType",
            "Name",
            "RelativePath",
            "FileName",
            "ParentFolderName",
            "MetadataLanguage",
            "Year",
            "SeasonNumber",
            "EpisodeNumber",
            "SeriesDisplayOrder",
            "ParsedName",
            "ParsedChineseName",
            "ParsedYear",
            "ParsedSeasonNumber",
            "ParsedEpisodeNumber",
            "ParsedIsSpecial",
            "ParsedIsExtra",
            "ExistingProviderIdsSummary",
        };

        [TestMethod]
        public void BuildJson_ContainsOnlyWhitelistedFieldsAndNoSensitiveValues()
        {
            var info = new EpisodeInfo
            {
                Name = "第 1 集",
                Path = @"\\NAS\share\Shows\三体\Season 01\S01E01.mkv",
                MetadataLanguage = "zh-CN",
                Year = 2023,
                ParentIndexNumber = 1,
                IndexNumber = 1,
                SeriesDisplayOrder = "absolute",
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "DoubanID-具体值-123" },
                    { MetadataProvider.Tmdb.ToString(), "Tmdb-具体值-456" },
                    { MetadataProvider.Imdb.ToString(), "tt-secret" },
                },
                SeriesProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tvdb.ToString(), "tvdb-secret" },
                },
            };
            var parsed = new ParseNameResult
            {
                Name = "Three Body",
                ChineseName = "三体",
                Year = 2023,
                ParentIndexNumber = 1,
                IndexNumber = 1,
                AnimeType = "SP",
            };

            var json = LlmPromptContextBuilder.BuildJson(
                info,
                "Episode",
                new[] { @"\\NAS\share", "/mnt/media", "/root", "/home/test", "/opt/jellyfin" },
                parsed);

            using var document = JsonDocument.Parse(json);
            var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            CollectionAssert.IsSubsetOf(propertyNames, AllowedFields.ToArray());
            CollectionAssert.AreEquivalent(AllowedFields.ToArray(), propertyNames);

            AssertSafePromptJson(json);
            Assert.IsTrue(json.Contains("\"Douban\":true", System.StringComparison.Ordinal), json);
            Assert.IsTrue(json.Contains("\"Tmdb\":true", System.StringComparison.Ordinal), json);
            Assert.IsTrue(json.Contains("\"Tvdb\":true", System.StringComparison.Ordinal), json);
            Assert.IsTrue(json.Contains("\"Imdb\":true", System.StringComparison.Ordinal), json);
        }

        [TestMethod]
        public void Build_UsesParserFieldsAndSanitizedPathFields()
        {
            var info = new MovieInfo
            {
                Name = "Inception",
                Path = "/mnt/media/Movies/Inception (2010)/Inception.mkv",
                MetadataLanguage = "en-US",
                Year = 2010,
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), "27205" },
                },
            };
            var parsed = NameParser.Parse("Inception (2010)");

            var context = LlmPromptContextBuilder.Build(
                info,
                "Movie",
                new[] { "/mnt/media" },
                parsed);

            Assert.AreEqual("Movie", context.MediaType);
            Assert.AreEqual("Inception", context.Name);
            Assert.AreEqual("Movies/Inception (2010)/Inception.mkv", context.RelativePath);
            Assert.AreEqual("Inception.mkv", context.FileName);
            Assert.AreEqual("Inception (2010)", context.ParentFolderName);
            Assert.AreEqual("en-US", context.MetadataLanguage);
            Assert.AreEqual(2010, context.Year);
            Assert.AreEqual("Inception", context.ParsedName);
            Assert.AreEqual(2010, context.ParsedYear);
            Assert.IsTrue(context.ExistingProviderIdsSummary.Tmdb);
            Assert.IsFalse(context.ExistingProviderIdsSummary.Douban);
        }

        private static void AssertSafePromptJson(string json)
        {
            var forbiddenFragments = new[]
            {
                "/mnt/media",
                "/root",
                "/home",
                "/opt/jellyfin",
                "C:\\",
                "\\\\NAS",
                "DoubanID-具体值-123",
                "Tmdb-具体值-456",
                "sk-test-secret-123456",
                "cookie",
                "token",
                "<plot>",
            };

            foreach (var fragment in forbiddenFragments)
            {
                Assert.IsFalse(json.Contains(fragment, System.StringComparison.Ordinal), json);
            }
        }
    }
}
