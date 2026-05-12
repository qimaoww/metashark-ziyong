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
            Assert.IsTrue(block.Contains("关闭相对路径上下文后不会发送相对路径、文件名或目录结构", StringComparison.Ordinal), "说明必须标明关闭相对路径上下文后的发送边界。");
            Assert.IsTrue(block.Contains("TMDb 返回的候选剧集组", StringComparison.Ordinal), "说明必须标明只从 TMDb 候选组中选择。");
            Assert.IsTrue(block.Contains("不会发送完整本地路径、API Key、cookie 或 token", StringComparison.Ordinal), "说明必须标明不会发送完整本地路径和敏感信息。");
            Assert.IsTrue(block.Contains("不会写入 ProviderIds", StringComparison.Ordinal), "说明必须标明不会写入 ProviderIds。");
        }

        [TestMethod]
        public void ShouldRenderTuningInputsBelowEpisodeGroupMappingAssistToggle()
        {
            var html = ReadConfigPageHtml();
            var toggleIndex = html.IndexOf("id=\"EnableLlmEpisodeGroupMappingAssist\"", StringComparison.Ordinal);
            var minConfidenceIndex = html.IndexOf("id=\"LlmEpisodeGroupMappingMinConfidence\"", StringComparison.Ordinal);
            var maxCandidateGroupsIndex = html.IndexOf("id=\"LlmEpisodeGroupMappingMaxCandidateGroups\"", StringComparison.Ordinal);
            var backfillIndex = html.IndexOf("id=\"EnableSearchMissingMetadataEpisodeTitleBackfill\"", StringComparison.Ordinal);

            Assert.IsTrue(minConfidenceIndex > toggleIndex, "剧集组映射专用置信度输入必须位于 LLM 剧集组映射开关下方。");
            Assert.IsTrue(maxCandidateGroupsIndex > minConfidenceIndex, "剧集组映射候选组上限输入必须位于专用置信度输入下方。");
            Assert.IsTrue(backfillIndex > maxCandidateGroupsIndex, "剧集组映射调参项应位于其它高级项前。");
            AssertNumberInput(html, "LlmEpisodeGroupMappingMinConfidence", "0", "1", "0.01");
            AssertNumberInput(html, "LlmEpisodeGroupMappingMaxCandidateGroups", "1", "50", "1");
            Assert.IsTrue(html.Contains("用于自动写回 TMDb 剧集组映射的最低置信度，允许范围 0.0-1.0，默认 0.80。", StringComparison.Ordinal), "置信度输入必须说明用途、范围和默认值。");
            Assert.IsTrue(html.Contains("发送给 LLM 的 TMDb 剧集组候选数量上限，允许范围 1-50，默认 8。", StringComparison.Ordinal), "候选组上限输入必须说明用途、范围和默认值。");
        }

        [TestMethod]
        public void ShouldRoundTripEpisodeGroupAssistFieldsThroughPageshowAndSubmit()
        {
            var html = ReadConfigPageHtml();

            Assert.AreEqual(2, CountOccurrences(html, "config.EnableLlmEpisodeGroupMappingAssist"), "开关应只在 pageshow 和 submit 各绑定一次。 ");
            Assert.IsTrue(html.Contains("document.querySelector('#EnableLlmEpisodeGroupMappingAssist').checked = config.EnableLlmEpisodeGroupMappingAssist;", StringComparison.Ordinal));
            Assert.IsTrue(html.Contains("config.EnableLlmEpisodeGroupMappingAssist = document.querySelector('#EnableLlmEpisodeGroupMappingAssist').checked;", StringComparison.Ordinal));
            AssertRoundTripNumber(html, "LlmEpisodeGroupMappingMinConfidence");
            AssertRoundTripNumber(html, "LlmEpisodeGroupMappingMaxCandidateGroups");
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

        private static void AssertNumberInput(string html, string id, string min, string max, string step)
        {
            var match = Regex.Match(
                html,
                $@"<input[^>]*id=""{id}""[^>]*name=""{id}""[^>]*type=""number""[^>]*>",
                RegexOptions.Singleline);

            Assert.IsTrue(match.Success, $"配置页缺少 {id} number 输入。");
            Assert.IsTrue(match.Value.Contains($@"min=""{min}""", StringComparison.Ordinal), $"{id} 必须声明 min={min}。");
            Assert.IsTrue(match.Value.Contains($@"max=""{max}""", StringComparison.Ordinal), $"{id} 必须声明 max={max}。");
            Assert.IsTrue(match.Value.Contains($@"step=""{step}""", StringComparison.Ordinal), $"{id} 必须声明 step={step}。");
        }

        private static void AssertRoundTripNumber(string html, string propertyName)
        {
            Assert.AreEqual(2, CountOccurrences(html, $"config.{propertyName}"), $"{propertyName} 应只在 pageshow 和 submit 各绑定一次。");
            Assert.IsTrue(html.Contains($"document.querySelector('#{propertyName}').value = config.{propertyName};", StringComparison.Ordinal), $"{propertyName} pageshow 必须回填。");
            Assert.IsTrue(html.Contains($"config.{propertyName} = document.querySelector('#{propertyName}').valueAsNumber;", StringComparison.Ordinal), $"{propertyName} submit 必须保存 number。");
        }
    }
}
