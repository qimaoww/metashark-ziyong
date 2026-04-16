using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class ConfigPageDefaultScraperContractTest
    {
        private const string PropertyName = "DefaultScraperMode";
        private const string SelectId = "DefaultScraperMode";
        private const string DefaultMode = "default";
        private const string TmdbOnlyMode = "tmdb-only";

        private static readonly string ConfigPagePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../Jellyfin.Plugin.MetaShark/Configuration/configPage.html"));

        [TestMethod]
        public void OnlyTwoOptions_ShouldExposeExactlyTwoChineseChoicesWithStableValues()
        {
            var selectBlock = GetDefaultScraperSelectBlock(ReadConfigPageHtml());
            var options = Regex.Matches(selectBlock, @"<option\s+value=""([^""]+)"">\s*([^<]+?)\s*</option>");

            Assert.AreEqual(2, options.Count, "DefaultScraperMode 下拉框必须且仅允许两个选项。");
            Assert.AreEqual(DefaultMode, options[0].Groups[1].Value, "第一个选项的稳定值必须为 default。");
            Assert.AreEqual("默认", options[0].Groups[2].Value.Trim(), "第一个选项文案必须为“默认”。");
            Assert.AreEqual(TmdbOnlyMode, options[1].Groups[1].Value, "第二个选项的稳定值必须为 tmdb-only。");
            Assert.AreEqual("仅 TMDB", options[1].Groups[2].Value.Trim(), "第二个选项文案必须为“仅 TMDB”。");
            Assert.IsFalse(selectBlock.Contains("<option value=\"默认\">", StringComparison.Ordinal), "中文标签不得作为持久化 value。");
            Assert.IsFalse(selectBlock.Contains("<option value=\"仅 TMDB\">", StringComparison.Ordinal), "中文标签不得作为持久化 value。");
        }

        [TestMethod]
        public void RoundTrip_ShouldLoadAndSaveThroughSameConfigurationField()
        {
            var html = ReadConfigPageHtml();
            var loadBinding = $"document.querySelector('#{SelectId}').value = config.{PropertyName};";
            var saveBinding = $"config.{PropertyName} = document.querySelector('#{SelectId}').value;";

            Assert.AreEqual(2, CountOccurrences(html, $"config.{PropertyName}"), "DefaultScraperMode 在配置页脚本中应只用于一次读取和一次保存绑定。");
            Assert.IsTrue(html.Contains(loadBinding, StringComparison.Ordinal), "pageshow 时必须从 config.DefaultScraperMode 回填 select.value。");
            Assert.IsTrue(html.Contains(saveBinding, StringComparison.Ordinal), "submit 时必须把 select.value 回写到 config.DefaultScraperMode。");
            Assert.IsFalse(html.Contains($"config.{PropertyName} = '默认'", StringComparison.Ordinal), "不得把中文标签写回配置对象。");
            Assert.IsFalse(html.Contains($"config.{PropertyName} = '仅 TMDB'", StringComparison.Ordinal), "不得把中文标签写回配置对象。");
        }

        [TestMethod]
        public void RoundTrip_ShouldUseSelectValueInsteadOfVisibleChineseLabel()
        {
            var html = ReadConfigPageHtml();

            Assert.IsTrue(html.Contains($"document.querySelector('#{SelectId}').value", StringComparison.Ordinal), "DefaultScraperMode round-trip 必须依赖 select.value。");
            Assert.IsFalse(Regex.IsMatch(html, $@"{PropertyName}\s*=\s*document\.querySelector\('#{SelectId}'\)\.(innerText|textContent|selectedOptions\[0\]\.text)"), "配置保存不得依赖中文显示文本。");
        }

        [TestMethod]
        public void DefaultScraperMode_ShouldAppearInsideAdvancedSettingsAfterEpisodeTitleBackfill()
        {
            var html = ReadConfigPageHtml();
            const string advancedSettingsHeading = "<h3>高级设置</h3>";
            const string backfillId = "EnableSearchMissingMetadataEpisodeTitleBackfill";
            var advancedSettingsBlock = GetFieldsetBlock(html, advancedSettingsHeading);

            Assert.IsFalse(
                html[..html.IndexOf(advancedSettingsHeading, StringComparison.Ordinal)].Contains($"id=\"{SelectId}\"", StringComparison.Ordinal),
                "DefaultScraperMode 不得再出现在“高级设置”标题之前。");
            Assert.IsTrue(advancedSettingsBlock.Contains($"id=\"{SelectId}\"", StringComparison.Ordinal), "DefaultScraperMode 必须位于“高级设置”分组内。");
            Assert.IsTrue(
                advancedSettingsBlock.IndexOf($"id=\"{SelectId}\"", StringComparison.Ordinal) > advancedSettingsBlock.LastIndexOf(backfillId, StringComparison.Ordinal),
                "DefaultScraperMode 必须位于 EnableSearchMissingMetadataEpisodeTitleBackfill 之后。");
        }

        [TestMethod]
        public void DefaultScraperMode_ShouldRenderApprovedDescriptionUnderSelect()
        {
            var advancedSettingsBlock = GetFieldsetBlock(ReadConfigPageHtml(), "<h3>高级设置</h3>");
            const string expectedDescription = "默认：优先使用豆瓣刮削，缺失内容再由 TMDB 补充；仅 TMDB：只使用 TMDB 刮削，忽略豆瓣。";

            var match = Regex.Match(
                advancedSettingsBlock,
                @"<div class=""selectContainer"">.*?<label class=""selectLabel"" for=""DefaultScraperMode"">默认刮削器：</label>.*?<select[^>]*id=""DefaultScraperMode""[^>]*>.*?</select>\s*<div class=""fieldDescription"">\s*(.*?)\s*</div>\s*</div>",
                RegexOptions.Singleline);

            Assert.IsTrue(match.Success, "DefaultScraperMode 下拉框下方必须存在字段说明。");
            Assert.AreEqual(expectedDescription, Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim(), "DefaultScraperMode 字段说明文案必须与批准版本完全一致。");
        }

        private static string ReadConfigPageHtml()
        {
            return File.ReadAllText(ConfigPagePath);
        }

        private static string GetDefaultScraperSelectBlock(string html)
        {
            var match = Regex.Match(
                html,
                @"<select[^>]*id=""DefaultScraperMode""[^>]*>\s*(.*?)\s*</select>",
                RegexOptions.Singleline);

            Assert.IsTrue(match.Success, "configPage.html 缺少 DefaultScraperMode 下拉框。");
            return match.Value;
        }

        private static string GetFieldsetBlock(string html, string heading)
        {
            var match = Regex.Match(
                html,
                $@"<fieldset[^>]*>\s*<legend>\s*{Regex.Escape(heading)}\s*</legend>(.*?)</fieldset>",
                RegexOptions.Singleline);

            Assert.IsTrue(match.Success, $"configPage.html 缺少 {heading} 分组。");
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
