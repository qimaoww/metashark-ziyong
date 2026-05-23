using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;

namespace Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping
{
    [TestClass]
    [TestCategory("Stable")]
    public class EpisodeGroupMappingFacadeTest
    {
        [TestMethod]
        public void TryGetEffectiveGroupId_WhenManualMappingExists_ReturnsManualGroupId()
        {
            var configuration = new PluginConfiguration
            {
                TmdbEpisodeGroupMap = "70000=group-manual",
                LlmTmdbEpisodeGroupMap = "70000=group-llm",
            };
            var facade = new EpisodeGroupMappingFacade();

            var found = facade.TryGetEffectiveGroupId(configuration, "70000", out var groupId);

            Assert.IsTrue(found);
            Assert.AreEqual("group-manual", groupId);
        }

        [TestMethod]
        public void TryGetEffectiveGroupId_WhenManualMissing_ReturnsLlmGroupId()
        {
            var configuration = new PluginConfiguration
            {
                TmdbEpisodeGroupMap = string.Empty,
                LlmTmdbEpisodeGroupMap = "70000=group-llm",
            };
            var facade = new EpisodeGroupMappingFacade();

            var found = facade.TryGetEffectiveGroupId(configuration, "70000", out var groupId);

            Assert.IsTrue(found);
            Assert.AreEqual("group-llm", groupId);
        }

        [TestMethod]
        public void TryGetManualGroupId_WhenMapped_ReturnsManualGroupId()
        {
            var configuration = new PluginConfiguration
            {
                TmdbEpisodeGroupMap = "70000=group-manual",
                LlmTmdbEpisodeGroupMap = "70000=group-llm",
            };
            var facade = new EpisodeGroupMappingFacade();

            var found = facade.TryGetManualGroupId(configuration, "70000", out var groupId);

            Assert.IsTrue(found);
            Assert.AreEqual("group-manual", groupId);
        }

        [TestMethod]
        public void GetEffectiveMappingText_WhenManualAndLlmPresent_ReturnsManualOverlayText()
        {
            var configuration = new PluginConfiguration
            {
                TmdbEpisodeGroupMap = "70000=group-manual",
                LlmTmdbEpisodeGroupMap = "65942=group-llm",
            };
            var facade = new EpisodeGroupMappingFacade();

            var effectiveText = facade.GetEffectiveMappingText(configuration);

            Assert.AreEqual("65942=group-llm\n70000=group-manual", effectiveText);
        }

        [TestMethod]
        public void GetEffectiveMappingText_WithManualAndLlmStrings_ReturnsManualOverlayText()
        {
            var facade = new EpisodeGroupMappingFacade();

            var effectiveText = facade.GetEffectiveMappingText("70000=group-manual", "65942=group-llm");

            Assert.AreEqual("65942=group-llm\n70000=group-manual", effectiveText);
        }
    }
}
