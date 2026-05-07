using Jellyfin.Plugin.MetaShark.Providers.Llm;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmScrapeMismatchDetectorTest
    {
        [TestMethod]
        public void Detect_WhenTitleTokensAreDisjoint_ReturnsMismatch()
        {
            var result = Detect(CreateContext("Movie", name: "Inception", parsedName: "Inception"), new LlmScrapingSuggestion { Title = "Interstellar", Confidence = 0.9 });

            Assert.IsTrue(result.IsMismatch);
            Assert.AreEqual("TitleTokensDisjoint", result.Reason);
        }

        [TestMethod]
        public void Detect_WhenYearDiffersByMoreThanOne_ReturnsMismatch()
        {
            var result = Detect(CreateContext("Movie", name: "Inception", year: 2010), new LlmScrapingSuggestion { Title = "Inception", Year = 2013, Confidence = 0.9 });

            Assert.IsTrue(result.IsMismatch);
            Assert.AreEqual("YearMismatch", result.Reason);
        }

        [TestMethod]
        public void Detect_WhenSeasonOrEpisodeConflicts_ReturnsMismatch()
        {
            var context = CreateContext("Episode", name: "三体", seasonNumber: 1, episodeNumber: 2);

            var result = Detect(context, new LlmScrapingSuggestion { Title = "三体", SeasonNumber = 1, EpisodeNumber = 3, Confidence = 0.9 });

            Assert.IsTrue(result.IsMismatch);
            Assert.AreEqual("SeasonEpisodeMismatch", result.Reason);
        }

        [TestMethod]
        public void Detect_WhenMovieAndSeriesStructureConflicts_ReturnsMismatch()
        {
            var result = Detect(CreateContext("Movie", name: "三体"), new LlmScrapingSuggestion { MediaType = "Series", Title = "三体", Confidence = 0.9 });

            Assert.IsTrue(result.IsMismatch);
            Assert.AreEqual("MediaStructureMismatch", result.Reason);
        }

        [TestMethod]
        public void Detect_WhenDefaultEpisodeTitleHasValidHumanSuggestion_DoesNotRequireEpisodeNumber()
        {
            var context = CreateContext("Episode", name: "第 7 集", episodeNumber: 7);

            var result = Detect(context, new LlmScrapingSuggestion { Title = "启程", Confidence = 0.9 });

            Assert.IsFalse(result.IsMismatch, result.Reason);
        }

        [DataTestMethod]
        [DataRow("三体", "三体：周年纪念版")]
        [DataRow("Shingeki no Kyojin", "Attack on Shingeki")]
        [DataRow("君の名は", "君の名は。")]
        public void Detect_NormalizesChineseEnglishAndJapaneseTitles(string contextTitle, string suggestionTitle)
        {
            var result = Detect(CreateContext("Movie", name: contextTitle, parsedName: contextTitle), new LlmScrapingSuggestion { Title = suggestionTitle, Confidence = 0.9 });

            Assert.IsFalse(result.IsMismatch, result.Reason);
        }

        [TestMethod]
        public void Detect_IgnoresShortTokenNoise()
        {
            var result = Detect(CreateContext("Movie", name: "It", parsedName: "It"), new LlmScrapingSuggestion { Title = "It", Year = 2017, Confidence = 0.9 });

            Assert.IsFalse(result.IsMismatch, result.Reason);
        }

        private static LlmMismatchResult Detect(LlmPromptContext context, LlmScrapingSuggestion suggestion)
        {
            return new LlmScrapeMismatchDetector().Detect(context, suggestion);
        }

        private static LlmPromptContext CreateContext(string mediaType, string name, string? parsedName = null, int? year = null, int? seasonNumber = null, int? episodeNumber = null)
        {
            return new LlmPromptContext
            {
                MediaType = mediaType,
                Name = name,
                ParentFolderName = name,
                ParsedName = parsedName,
                Year = year,
                ParsedYear = year,
                SeasonNumber = seasonNumber,
                ParsedSeasonNumber = seasonNumber,
                EpisodeNumber = episodeNumber,
                ParsedEpisodeNumber = episodeNumber,
            };
        }
    }
}
