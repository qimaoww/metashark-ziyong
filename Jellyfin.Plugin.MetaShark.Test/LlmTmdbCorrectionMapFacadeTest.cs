using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers.Llm;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class LlmTmdbCorrectionMapFacadeTest
    {
        [TestMethod]
        public void TryGetCorrection_WhenEnabledAndMapped_ReturnsTmdbId()
        {
            var configuration = new PluginConfiguration
            {
                EnableLlmTmdbCorrectionPersistence = true,
                LlmTmdbCorrectionMap = "series:douban:26862290=tmdb:65942",
            };
            var facade = new LlmTmdbCorrectionMapFacade();

            var found = facade.TryGetCorrection(configuration, "Series", "26862290", out var tmdbId);

            Assert.IsTrue(found);
            Assert.AreEqual("65942", tmdbId);
        }

        [TestMethod]
        public void TryGetCorrection_WhenDisabled_ReturnsFalse()
        {
            var configuration = new PluginConfiguration
            {
                EnableLlmTmdbCorrectionPersistence = false,
                LlmTmdbCorrectionMap = "series:douban:26862290=tmdb:65942",
            };
            var facade = new LlmTmdbCorrectionMapFacade();

            var found = facade.TryGetCorrection(configuration, "Series", "26862290", out var tmdbId);

            Assert.IsFalse(found);
            Assert.AreEqual(string.Empty, tmdbId);
        }

        [TestMethod]
        public void TryGetCompletion_WhenEnabledAndMapped_ReturnsTmdbId()
        {
            var configuration = new PluginConfiguration
            {
                EnableLlmTmdbCompletionPersistence = true,
                LlmTmdbCompletionMap = "movie:douban:1292052=tmdb:13",
            };
            var facade = new LlmTmdbCorrectionMapFacade();

            var found = facade.TryGetCompletion(configuration, "Movie", "1292052", out var tmdbId);

            Assert.IsTrue(found);
            Assert.AreEqual("13", tmdbId);
        }

        [TestMethod]
        public void TryGetCompletion_WhenDisabled_ReturnsFalse()
        {
            var configuration = new PluginConfiguration
            {
                EnableLlmTmdbCompletionPersistence = false,
                LlmTmdbCompletionMap = "movie:douban:1292052=tmdb:13",
            };
            var facade = new LlmTmdbCorrectionMapFacade();

            var found = facade.TryGetCompletion(configuration, "Movie", "1292052", out var tmdbId);

            Assert.IsFalse(found);
            Assert.AreEqual(string.Empty, tmdbId);
        }

        [TestMethod]
        public void TryFindCorrectionByTmdbId_WhenEnabledAndMapped_ReturnsMatchingTmdbId()
        {
            var configuration = new PluginConfiguration
            {
                EnableLlmTmdbCorrectionPersistence = true,
                LlmTmdbCorrectionMap = "movie:douban:1292052=tmdb:13\nseries:douban:26862290=tmdb:65942",
            };
            var facade = new LlmTmdbCorrectionMapFacade();

            var found = facade.TryFindCorrectionByTmdbId(configuration, "Series", "65942", out var correctedTmdbId);

            Assert.IsTrue(found);
            Assert.AreEqual("65942", correctedTmdbId);
        }

        [TestMethod]
        public void TryFindCorrectionByTmdbId_WhenMediaTypeDoesNotMatch_ReturnsFalse()
        {
            var configuration = new PluginConfiguration
            {
                EnableLlmTmdbCorrectionPersistence = true,
                LlmTmdbCorrectionMap = "movie:douban:1292052=tmdb:13",
            };
            var facade = new LlmTmdbCorrectionMapFacade();

            var found = facade.TryFindCorrectionByTmdbId(configuration, "Series", "13", out var correctedTmdbId);

            Assert.IsFalse(found);
            Assert.AreEqual(string.Empty, correctedTmdbId);
        }
    }
}
