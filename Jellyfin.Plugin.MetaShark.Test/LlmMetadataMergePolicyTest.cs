using System.Collections.Generic;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmMetadataMergePolicyTest
    {
        [TestMethod]
        public void Apply_DoesNotModifyProviderIds()
        {
            var result = new MetadataResult<Movie>
            {
                Item = new Movie
                {
                    ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), "27205" } },
                },
            };

            new LlmMetadataMergePolicy().Apply(result, CreateSuggestion(title: "盗梦空间"), CreateConfiguration(allowTextCompletion: true));

            Assert.AreEqual("27205", result.Item!.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual(1, result.Item.ProviderIds.Count);
        }

        [TestMethod]
        public void Apply_DoesNotModifySeasonOrEpisodeNumbers()
        {
            var result = new MetadataResult<Episode>
            {
                Item = new Episode
                {
                    ParentIndexNumber = 1,
                    IndexNumber = 2,
                },
            };

            new LlmMetadataMergePolicy().Apply(result, CreateSuggestion(title: "第二集", seasonNumber: 9, episodeNumber: 9), CreateConfiguration(allowTextCompletion: true));

            Assert.AreEqual(1, result.Item!.ParentIndexNumber);
            Assert.AreEqual(2, result.Item.IndexNumber);
        }

        [TestMethod]
        public void Apply_DoesNotOverwriteNonEmptyAuthoritativeFields()
        {
            var result = new MetadataResult<Series>
            {
                Item = new Series
                {
                    Name = "三体",
                    OriginalTitle = "Three-Body",
                    Overview = "权威简介",
                    Tagline = "权威标语",
                },
            };

            var merge = new LlmMetadataMergePolicy().Apply(result, CreateSuggestion("三体 LLM", "LLM Original", "LLM Overview"), CreateConfiguration(allowTextCompletion: true));

            Assert.IsFalse(merge.Applied);
            Assert.AreEqual("三体", result.Item!.Name);
            Assert.AreEqual("Three-Body", result.Item.OriginalTitle);
            Assert.AreEqual("权威简介", result.Item.Overview);
            Assert.AreEqual("权威标语", result.Item.Tagline);
        }

        [TestMethod]
        public void Apply_WhenTextCompletionDisabled_DoesNotFillGeneratedTextFields()
        {
            var result = new MetadataResult<Movie> { Item = new Movie() };

            var merge = new LlmMetadataMergePolicy().Apply(result, CreateSuggestion("盗梦空间", "Inception", "简介"), CreateConfiguration(allowTextCompletion: false));

            Assert.IsFalse(merge.Applied);
            Assert.IsNull(result.Item!.Name);
            Assert.IsNull(result.Item.OriginalTitle);
            Assert.IsNull(result.Item.Overview);
            Assert.IsNull(result.Item.Tagline);
        }

        [TestMethod]
        public void Apply_WhenTextCompletionEnabled_FillsOnlyWhitelistedEmptyFields()
        {
            var result = new MetadataResult<Movie>
            {
                Item = new Movie
                {
                    CommunityRating = 8.8f,
                    OfficialRating = "PG-13",
                    PremiereDate = new DateTime(2010, 7, 16),
                    Genres = new[] { "科幻" },
                },
                People = new List<PersonInfo> { new PersonInfo { Name = "Christopher Nolan" } },
            };

            var merge = new LlmMetadataMergePolicy().Apply(result, CreateSuggestion("盗梦空间", "Inception", "简介"), CreateConfiguration(allowTextCompletion: true));

            Assert.IsTrue(merge.Applied);
            CollectionAssert.AreEquivalent(new[] { "Name", "OriginalTitle", "Overview" }, merge.ChangedFields.ToArray());
            Assert.AreEqual("盗梦空间", result.Item!.Name);
            Assert.AreEqual("Inception", result.Item.OriginalTitle);
            Assert.AreEqual("简介", result.Item.Overview);
            Assert.IsNull(result.Item.Tagline);
            Assert.AreEqual(8.8f, result.Item.CommunityRating);
            Assert.AreEqual("PG-13", result.Item.OfficialRating);
            Assert.AreEqual(new DateTime(2010, 7, 16), result.Item.PremiereDate);
            CollectionAssert.AreEqual(new[] { "科幻" }, result.Item.Genres);
            Assert.AreEqual(1, result.People.Count);
        }

        [TestMethod]
        public void Apply_WhenConfidenceBelowThreshold_DropsSuggestion()
        {
            var result = new MetadataResult<Movie> { Item = new Movie() };
            var suggestion = CreateSuggestion(title: "盗梦空间");
            suggestion.Confidence = 0.5;

            var merge = new LlmMetadataMergePolicy().Apply(result, suggestion, CreateConfiguration(allowTextCompletion: true));
            var hints = new LlmMetadataMergePolicy().CreateSearchHints(suggestion, CreateConfiguration(allowTextCompletion: true));

            Assert.IsFalse(merge.Applied);
            Assert.AreEqual("ConfidenceBelowThreshold", merge.Reason);
            Assert.IsNull(result.Item!.Name);
            Assert.IsFalse(hints.HasHints);
        }

        [TestMethod]
        public void CreateSearchHints_UsesOnlyTitleAndYearForProviderQueries()
        {
            var hints = new LlmMetadataMergePolicy().CreateSearchHints(CreateSuggestion(title: "盗梦空间", year: 2010), CreateConfiguration(allowTextCompletion: false));

            Assert.IsTrue(hints.HasHints);
            Assert.AreEqual("盗梦空间", hints.Title);
            Assert.AreEqual(2010, hints.Year);
        }

        private static PluginConfiguration CreateConfiguration(bool allowTextCompletion)
        {
            return new PluginConfiguration
            {
                LlmAllowTextCompletion = allowTextCompletion,
                LlmConfidenceThreshold = 0.75,
            };
        }

        [TestMethod]
        public void Apply_DoesNotChangeExistingTagline()
        {
            var result = new MetadataResult<Movie>
            {
                Item = new Movie
                {
                    Tagline = "权威标语",
                },
            };

            var merge = new LlmMetadataMergePolicy().Apply(result, CreateSuggestion(title: "盗梦空间", originalTitle: "Inception", overview: "简介"), CreateConfiguration(allowTextCompletion: true));

            Assert.IsTrue(merge.Applied);
            Assert.AreEqual("权威标语", result.Item!.Tagline);
            CollectionAssert.DoesNotContain(merge.ChangedFields.ToArray(), "Tagline");
        }

        private static LlmScrapingSuggestion CreateSuggestion(string? title = null, string? originalTitle = null, string? overview = null, int? year = null, int? seasonNumber = null, int? episodeNumber = null)
        {
            return new LlmScrapingSuggestion
            {
                MediaType = "Movie",
                Title = title,
                OriginalTitle = originalTitle,
                Overview = overview,
                Year = year,
                SeasonNumber = seasonNumber,
                EpisodeNumber = episodeNumber,
                Confidence = 0.9,
            };
        }
    }
}
