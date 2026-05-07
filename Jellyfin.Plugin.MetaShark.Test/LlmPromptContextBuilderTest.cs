using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MetaShark;
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

        [TestMethod]
        public void BuildExternalIdPromptJson_IncludesSchemaInstructionAndSafeContext()
        {
            var info = new EpisodeInfo
            {
                Name = "The Matrix",
                Path = @"\\SECRET-SERVER\PRIVATE-SHARE\Shows\The Matrix\Season 01\S01E01.mkv",
                MetadataLanguage = "en-US",
                Year = 1999,
                ParentIndexNumber = 1,
                IndexNumber = 1,
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), "1399" },
                    { MetadataProvider.Tvdb.ToString(), "81189" },
                },
                SeriesProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Imdb.ToString(), "tt0133093" },
                    { BaseProvider.DoubanProviderId, "1291843" },
                },
            };

            var json = LlmPromptContextBuilder.BuildExternalIdPromptJson(
                info,
                "Episode",
                new[] { @"\\SECRET-SERVER\PRIVATE-SHARE", "/opt/jellyfin", "/home/test" },
                new[] { "/opt/jellyfin/media/Shows/The Matrix/Season 01/S01E02.mkv" });

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.AreEqual("Episode", root.GetProperty("MediaType").GetString());
            Assert.AreEqual("The Matrix", root.GetProperty("Title").GetString());
            Assert.AreEqual(1999, root.GetProperty("Year").GetInt32());
            Assert.AreEqual(1, root.GetProperty("SeasonNumber").GetInt32());
            Assert.AreEqual(1, root.GetProperty("EpisodeNumber").GetInt32());
            var outputSchema = root.GetProperty("OutputSchema").GetString();
            var constraints = root.GetProperty("Constraints").EnumerateArray().Select(constraint => constraint.GetString()).ToArray();
            Assert.IsTrue(outputSchema!.Contains("externalIdCandidates", System.StringComparison.Ordinal), outputSchema);
            Assert.IsTrue(constraints.Any(constraint => constraint!.Contains("output only external ID candidates", System.StringComparison.OrdinalIgnoreCase)), json);
            Assert.IsTrue(constraints.Any(constraint => constraint!.Contains("do not output title, overview, people, images, URLs", System.StringComparison.Ordinal)), json);
            Assert.IsTrue(constraints.Any(constraint => constraint!.Contains("{ \"externalIdCandidates\": [] }", System.StringComparison.Ordinal)), json);
            Assert.IsFalse(json.Contains("\"candidates\"", System.StringComparison.Ordinal), json);
            AssertSafePromptJson(json);
            Assert.IsFalse(json.Contains("SECRET-SERVER", System.StringComparison.Ordinal), json);
            Assert.IsFalse(json.Contains("PRIVATE-SHARE", System.StringComparison.Ordinal), json);

            var samples = root.GetProperty("SafeRelativePathSamples").EnumerateArray().Select(sample => sample.GetString()).ToArray();
            CollectionAssert.Contains(samples, "Shows/The Matrix/Season 01/S01E01.mkv");
            CollectionAssert.Contains(samples, "Shows/The Matrix/Season 01/S01E02.mkv");
        }

        [TestMethod]
        public void BuildExternalIdPromptJson_IncludesOnlyActualPublicProviderIds()
        {
            var info = new SeriesInfo
            {
                Name = "The Matrix",
                Path = "/mnt/media/Shows/The Matrix",
                Year = 1999,
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Imdb.ToString(), "TT0133093" },
                    { MetadataProvider.Tmdb.ToString(), "1399" },
                    { MetadataProvider.Tvdb.ToString(), "81189" },
                    { BaseProvider.DoubanProviderId, "1291843" },
                    { MetaSharkPlugin.ProviderId, "Tmdb_1399" },
                    { "JellyfinItemId", "bf9b5f13e69045118bbb6c592843f47e" },
                    { "ServerUrl", "http://192.168.31.40:58096/" },
                    { "apiKey", "sk-test-secret-123456" },
                    { "token", "token-secret" },
                    { "cookie", "cookie-secret" },
                    { "Path", "/opt/jellyfin/Shows/The Matrix" },
                    { "UserName", "test" },
                    { "ExternalTmdb", "not-numeric" },
                    { "ExternalTvdb", "0" },
                    { "ExternalImdb", "nm0000206" },
                },
            };

            var json = LlmPromptContextBuilder.BuildExternalIdPromptJson(info, "Series", new[] { "/mnt/media" });

            using var document = JsonDocument.Parse(json);
            var publicProviderIds = document.RootElement.GetProperty("PublicProviderIds");
            var names = publicProviderIds.EnumerateObject().Select(property => property.Name).ToArray();
            CollectionAssert.AreEquivalent(new[] { "TMDb", "IMDb", "TVDB", "Douban" }, names);
            Assert.AreEqual("1399", publicProviderIds.GetProperty("TMDb").GetString());
            Assert.AreEqual("tt0133093", publicProviderIds.GetProperty("IMDb").GetString());
            Assert.AreEqual("81189", publicProviderIds.GetProperty("TVDB").GetString());
            Assert.AreEqual("1291843", publicProviderIds.GetProperty("Douban").GetString());
            AssertSafeExternalIdPromptJson(json);
            Assert.IsFalse(json.Contains(MetaSharkPlugin.ProviderId, System.StringComparison.Ordinal), json);
            Assert.IsFalse(json.Contains("Tmdb_1399", System.StringComparison.Ordinal), json);
            Assert.IsFalse(json.Contains("bf9b5f13e69045118bbb6c592843f47e", System.StringComparison.Ordinal), json);
            Assert.IsFalse(json.Contains("http://192.168.31.40:58096", System.StringComparison.Ordinal), json);
            Assert.IsFalse(json.Contains("sk-test-secret-123456", System.StringComparison.Ordinal), json);
            Assert.IsFalse(json.Contains("token-secret", System.StringComparison.Ordinal), json);
            Assert.IsFalse(json.Contains("cookie-secret", System.StringComparison.Ordinal), json);
        }

        [TestMethod]
        public void BuildExternalIdPromptContext_AllowsOnlyPublicProviderKeysFromReadOnlyProviderIds()
        {
            var providerIds = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
            {
                { MetadataProvider.Tmdb.ToString(), "1399" },
                { MetadataProvider.Imdb.ToString(), "tt0133093" },
                { MetadataProvider.Tvdb.ToString(), "81189" },
                { BaseProvider.DoubanProviderId, "1291843" },
                { "ServerUrl", "http://192.168.31.40:58096/" },
            });
            var info = new MovieInfo
            {
                Name = "The Matrix",
                Path = "/mnt/media/Movies/The Matrix (1999)/The Matrix.mkv",
                ProviderIds = providerIds.ToDictionary(pair => pair.Key, pair => pair.Value),
            };

            var context = LlmPromptContextBuilder.BuildExternalIdPromptContext(info, "Movie", new[] { "/mnt/media" });

            CollectionAssert.AreEquivalent(new[] { "TMDb", "IMDb", "TVDB", "Douban" }, context.PublicProviderIds.Keys.ToArray());
            Assert.AreEqual("1399", context.PublicProviderIds["TMDb"]);
            Assert.AreEqual("tt0133093", context.PublicProviderIds["IMDb"]);
            Assert.AreEqual("81189", context.PublicProviderIds["TVDB"]);
            Assert.AreEqual("1291843", context.PublicProviderIds["Douban"]);
            CollectionAssert.AreEqual(new[] { "Movies/The Matrix (1999)/The Matrix.mkv" }, context.SafeRelativePathSamples.ToArray());
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

        private static void AssertSafeExternalIdPromptJson(string json)
        {
            var forbiddenFragments = new[]
            {
                "/opt/jellyfin",
                "/mnt/media",
                "/root",
                "/home",
                "C:\\",
                "\\\\NAS",
                "apiKey",
                "token",
                "cookie",
                "ServerUrl",
                "UserName",
                "JellyfinItemId",
                "nm0000206",
                "not-numeric",
            };

            foreach (var fragment in forbiddenFragments)
            {
                Assert.IsFalse(json.Contains(fragment, System.StringComparison.Ordinal), json);
            }
        }
    }
}
