using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class ConfigPageLlmEpisodeGroupMappingContractTest
    {
        private static readonly string ConfigPagePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../Jellyfin.Plugin.MetaShark/Configuration/configPage.html"));

        [TestMethod]
        public void ShouldRenderToggleImmediatelyBelowTmdbEpisodeGroupMap()
        {
            var html = ReadConfigPageHtml();
            var mapIndex = html.IndexOf("id=\"TmdbEpisodeGroupMap\"", StringComparison.Ordinal);
            var toggleIndex = html.IndexOf("id=\"EnableLlmEpisodeGroupMappingAssist\"", StringComparison.Ordinal);
            var backfillIndex = html.IndexOf("id=\"EnableSearchMissingMetadataEpisodeTitleBackfill\"", StringComparison.Ordinal);

            Assert.IsTrue(mapIndex >= 0, "配置页必须保留 TmdbEpisodeGroupMap textarea。");
            Assert.IsTrue(toggleIndex > mapIndex, "LLM 剧集组映射开关必须位于 TmdbEpisodeGroupMap 下方。");
            Assert.IsTrue(backfillIndex > toggleIndex, "LLM 剧集组映射开关应紧跟在 TmdbEpisodeGroupMap 后、其它高级项前。");
        }

        [TestMethod]
        public void ShouldUseMatchingIdNameAndExplainPrivacyCandidateOnlyDefaultOff()
        {
            var block = GetToggleBlock(ReadConfigPageHtml());

            Assert.IsTrue(
                Regex.IsMatch(block, @"<input[^>]*id=""EnableLlmEpisodeGroupMappingAssist""[^>]*name=""EnableLlmEpisodeGroupMappingAssist""[^>]*type=""checkbox""", RegexOptions.Singleline),
                "开关必须使用与配置属性一致的 id/name。 ");
            Assert.IsTrue(block.Contains("默认关闭", StringComparison.Ordinal), "说明必须标明默认关闭。");
            Assert.IsTrue(block.Contains("相对路径/文件摘要", StringComparison.Ordinal), "说明必须标明只发送相对路径/文件摘要。");
            Assert.IsTrue(block.Contains("TMDb 返回的候选剧集组", StringComparison.Ordinal), "说明必须标明只从 TMDb 候选组中选择。");
            Assert.IsTrue(block.Contains("不会发送绝对路径", StringComparison.Ordinal), "说明必须标明不会发送绝对路径。");
            Assert.IsTrue(block.Contains("不会写入 ProviderIds", StringComparison.Ordinal), "说明必须标明不会写入 ProviderIds。");
        }

        [TestMethod]
        public void ShouldRoundTripToggleThroughPageshowAndSubmit()
        {
            var html = ReadConfigPageHtml();

            Assert.AreEqual(2, CountOccurrences(html, "config.EnableLlmEpisodeGroupMappingAssist"), "开关应只在 pageshow 和 submit 各绑定一次。 ");
            Assert.IsTrue(html.Contains("document.querySelector('#EnableLlmEpisodeGroupMappingAssist').checked = config.EnableLlmEpisodeGroupMappingAssist;", StringComparison.Ordinal));
            Assert.IsTrue(html.Contains("config.EnableLlmEpisodeGroupMappingAssist = document.querySelector('#EnableLlmEpisodeGroupMappingAssist').checked;", StringComparison.Ordinal));
        }

        [TestMethod]
        public void ShouldPreserveTmdbEpisodeGroupMapSaveAndRefreshScript()
        {
            var html = ReadConfigPageHtml();

            Assert.IsTrue(html.Contains("lastEpisodeGroupMap = config.TmdbEpisodeGroupMap || '';", StringComparison.Ordinal));
            Assert.IsTrue(html.Contains("document.querySelector('#TmdbEpisodeGroupMap').value = lastEpisodeGroupMap;", StringComparison.Ordinal));
            Assert.IsTrue(html.Contains("var previousEpisodeGroupMap = config.TmdbEpisodeGroupMap || '';", StringComparison.Ordinal));
            Assert.IsTrue(html.Contains("config.TmdbEpisodeGroupMap = document.querySelector('#TmdbEpisodeGroupMap').value;", StringComparison.Ordinal));
            Assert.IsTrue(html.Contains("refreshSeriesIfMappingChanged(previousEpisodeGroupMap, config.TmdbEpisodeGroupMap);", StringComparison.Ordinal));
            Assert.IsTrue(html.Contains("url: '/plugin/metashark/tmdb/refresh-series'", StringComparison.Ordinal));
        }

        private static string ReadConfigPageHtml()
        {
            return File.ReadAllText(ConfigPagePath);
        }

        private static string GetToggleBlock(string html)
        {
            var match = Regex.Match(
                html,
                @"<div class=""checkboxContainer checkboxContainer-withDescription"">\s*<label class=""emby-checkbox-label"" for=""EnableLlmEpisodeGroupMappingAssist"">(.*?)</div>",
                RegexOptions.Singleline);

            Assert.IsTrue(match.Success, "配置页缺少 EnableLlmEpisodeGroupMappingAssist 开关。 ");
            return match.Value;
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            var startIndex = 0;

            while (true)
            {
                var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
                if (index < 0)
                {
                    return count;
                }

                count++;
                startIndex = index + value.Length;
            }
        }
    }
}
