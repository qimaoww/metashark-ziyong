using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class ConfigPageLlmAssistContractTest
    {
        private static readonly string ConfigPagePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../Jellyfin.Plugin.MetaShark/Configuration/configPage.html"));

        private static readonly string ReadmePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../README.md"));

        private static readonly string[] FieldNames =
        [
            "EnableLlmAssist",
            "LlmBaseUrl",
            "LlmApiKey",
            "LlmModel",
            "LlmTimeoutSeconds",
            "LlmMaxTokens",
            "LlmAllowRelativePathContext",
            "LlmAllowTextCompletion",
            "LlmConfidenceThreshold",
            "LlmStructuredOutputMode",
        ];

        [TestMethod]
        public void ShouldRenderLlmAssistGroupAfterDefaultScraperMode()
        {
            var html = ReadConfigPageHtml();
            var defaultScraperIndex = html.IndexOf("id=\"DefaultScraperMode\"", StringComparison.Ordinal);
            var llmHeadingIndex = html.IndexOf("<h3>LLM 辅助刮削</h3>", StringComparison.Ordinal);
            var llmBlock = GetLlmAssistBlock(html);

            Assert.IsTrue(defaultScraperIndex >= 0, "配置页必须保留 DefaultScraperMode。");
            Assert.IsTrue(llmHeadingIndex > defaultScraperIndex, "LLM 辅助刮削分组必须位于 DefaultScraperMode 之后。");

            foreach (var fieldName in FieldNames)
            {
                Assert.IsTrue(llmBlock.Contains($"id=\"{fieldName}\"", StringComparison.Ordinal), $"LLM 分组必须包含 {fieldName} 字段。");
            }
        }

        [TestMethod]
        public void ShouldUseMatchingIdsAndNamesForAllLlmFields()
        {
            var llmBlock = GetLlmAssistBlock(ReadConfigPageHtml());

            foreach (var fieldName in FieldNames)
            {
                Assert.IsTrue(
                    Regex.IsMatch(llmBlock, $@"<(input|select)[^>]*id=""{fieldName}""[^>]*name=""{fieldName}""", RegexOptions.Singleline),
                    $"{fieldName} 必须使用与配置属性一致的 id/name。");
            }
        }

        [TestMethod]
        public void ShouldRoundTripEveryLlmFieldThroughOneLoadAndOneSaveBinding()
        {
            var html = ReadConfigPageHtml();

            AssertRoundTrip(html, "EnableLlmAssist", ".checked");
            AssertRoundTrip(html, "LlmBaseUrl", ".value");
            AssertRoundTrip(html, "LlmModel", ".value");
            AssertRoundTrip(html, "LlmTimeoutSeconds", ".value", ".valueAsNumber");
            AssertRoundTrip(html, "LlmMaxTokens", ".value", ".valueAsNumber");
            AssertRoundTrip(html, "LlmAllowRelativePathContext", ".checked");
            AssertRoundTrip(html, "LlmAllowTextCompletion", ".checked");
            AssertRoundTrip(html, "LlmConfidenceThreshold", ".value", ".valueAsNumber");
            AssertRoundTrip(html, "LlmStructuredOutputMode", ".value");
            Assert.AreEqual(2, CountOccurrences(html, "config.LlmApiKey"), "LlmApiKey 应只用于 placeholder 判断和非空保存，避免 pageshow 泄露旧 key。");
        }

        [TestMethod]
        public void LlmApiKey_ShouldUsePasswordInputAndPreserveExistingKeyWhenLeftBlank()
        {
            var html = ReadConfigPageHtml();
            var apiKeyInput = Regex.Match(html, @"<input[^>]*id=""LlmApiKey""[^>]*type=""password""[^>]*>", RegexOptions.Singleline);

            Assert.IsTrue(apiKeyInput.Success, "LlmApiKey 必须使用 password input。");
            Assert.IsFalse(html.Contains("document.querySelector('#LlmApiKey').value = config.LlmApiKey", StringComparison.Ordinal), "pageshow 不得把旧 LLM API key 写入 input.value。");
            Assert.IsTrue(html.Contains("document.querySelector('#LlmApiKey').value = '';", StringComparison.Ordinal), "pageshow 必须清空 password 输入框。");
            Assert.IsTrue(html.Contains("document.querySelector('#LlmApiKey').placeholder = config.LlmApiKey ? '已配置，留空则保留' : '';", StringComparison.Ordinal), "已有 key 时 placeholder 必须提示留空保留。");
            Assert.IsTrue(html.Contains("var llmApiKey = document.querySelector('#LlmApiKey').value;", StringComparison.Ordinal), "submit 必须读取 password 输入值。");
            Assert.IsTrue(Regex.IsMatch(html, @"if\s*\(llmApiKey\)\s*{\s*config\.LlmApiKey\s*=\s*llmApiKey;\s*}"), "submit 必须仅在非空 password 时覆盖 LlmApiKey。");
        }

        [TestMethod]
        public void StructuredOutputMode_ShouldUseStableValuesInsteadOfChineseLabels()
        {
            var selectBlock = GetSelectBlock(ReadConfigPageHtml(), "LlmStructuredOutputMode");
            var options = Regex.Matches(selectBlock, @"<option\s+value=""([^""]+)"">\s*([^<]+?)\s*</option>");

            CollectionAssert.AreEqual(new[] { "json-schema", "json-object", "text-json" }, options.Select(option => option.Groups[1].Value).ToArray(), "结构化输出模式必须使用稳定 value。");
            Assert.IsFalse(selectBlock.Contains("value=\"JSON Schema\"", StringComparison.Ordinal), "不得使用显示标签作为持久化 value。");
            Assert.IsFalse(selectBlock.Contains("value=\"JSON Object\"", StringComparison.Ordinal), "不得使用显示标签作为持久化 value。");
            Assert.IsFalse(selectBlock.Contains("value=\"Text JSON\"", StringComparison.Ordinal), "不得使用显示标签作为持久化 value。");
        }

        [TestMethod]
        public void ShouldRenderApprovedLlmSafetyCopy()
        {
            var html = ReadConfigPageHtml();
            var llmBlock = GetLlmAssistBlock(html);

            Assert.IsTrue(llmBlock.Contains("默认关闭。开启后，只在手动识别、手动刷新或搜索缺失元数据等需要重新查询元数据的流程中按配置尝试调用；不会作为独立元数据源。", StringComparison.Ordinal), "LLM 辅助刮削必须说明默认关闭、触发范围和非独立元数据源。 ");
            Assert.IsTrue(llmBlock.Contains("OpenAI 兼容接口地址，需要填写到 /v1 级别；留空时不会调用 LLM。启用前请自行评估费用、隐私和网络风险。", StringComparison.Ordinal), "Base URL 必须说明 /v1 级别和费用、隐私、网络风险。");
            Assert.IsTrue(llmBlock.Contains("默认开启。仅发送相对媒体路径和必要摘要，不发送完整本地路径、API Key、cookie 或 token。", StringComparison.Ordinal), "相对路径开关必须说明发送边界和敏感信息排除范围。");
            Assert.IsTrue(llmBlock.Contains("默认关闭。开启后，LLM 生成文本仅可在置信度和合并检查通过后回填空白或低风险的标题、简介类字段；不会覆盖权威元数据。", StringComparison.Ordinal), "LLM 生成文本开关必须说明默认关闭、回填边界和不得覆盖权威元数据。");
            Assert.IsTrue(html.Contains("默认关闭。仅在手动识别、手动刷新或搜索缺失元数据触发时辅助判断，并且只能从 TMDb 返回的候选剧集组中选择；不会发送完整本地路径、API Key、cookie 或 token，也不会写入 ProviderIds。", StringComparison.Ordinal), "剧集组映射辅助必须说明默认关闭、触发范围、TMDb 候选限制和敏感信息排除范围。");
        }

        [TestMethod]
        public void Readme_ShouldDocumentLlmSafetyBoundaries()
        {
            var readme = File.ReadAllText(ReadmePath);

            Assert.IsTrue(readme.Contains("`LLM 辅助刮削`：默认关闭，不是独立元数据源；只会在手动识别、手动刷新或搜索缺失元数据等需要重新查询元数据的流程中按配置尝试调用。", StringComparison.Ordinal), "README 必须说明 LLM 默认关闭、非独立元数据源和 eligible 触发范围。 ");
            Assert.IsTrue(readme.Contains("LLM 请求只发送相对媒体路径和必要摘要，不发送完整本地路径、API Key、cookie 或 token；启用前请自行评估费用、隐私和网络风险。", StringComparison.Ordinal), "README 必须说明 LLM 请求隐私边界和风险。 ");
            Assert.IsTrue(readme.Contains("OpenAI 兼容 Base URL 需要填写到 `/v1` 级别", StringComparison.Ordinal), "README 必须说明 OpenAI 兼容 Base URL 填到 /v1 级别。 ");
            Assert.IsTrue(readme.Contains("`LLM 辅助建议 TMDb 剧集组映射`：默认关闭，只能从 TMDb 返回的候选剧集组中选择，不承诺一定匹配正确。", StringComparison.Ordinal), "README 必须说明剧集组映射辅助只从 TMDb 候选中选择。 ");
        }

        private static string ReadConfigPageHtml()
        {
            return File.ReadAllText(ConfigPagePath);
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

        private static string GetLlmAssistBlock(string html)
        {
            var match = Regex.Match(
                html,
                @"<fieldset[^>]*>\s*<legend>\s*<h3>LLM 辅助刮削</h3>\s*</legend>(.*?)</fieldset>",
                RegexOptions.Singleline);

            Assert.IsTrue(match.Success, "configPage.html 缺少 LLM 辅助刮削分组。");
            return match.Value;
        }

        private static string GetSelectBlock(string html, string selectId)
        {
            var match = Regex.Match(
                html,
                $@"<select[^>]*id=""{selectId}""[^>]*>\s*(.*?)\s*</select>",
                RegexOptions.Singleline);

            Assert.IsTrue(match.Success, $"configPage.html 缺少 {selectId} 下拉框。");
            return match.Value;
        }

        private static void AssertRoundTrip(string html, string propertyName, string valueAccessor)
        {
            AssertRoundTrip(html, propertyName, valueAccessor, valueAccessor);
        }

        private static void AssertRoundTrip(string html, string propertyName, string loadAccessor, string saveAccessor)
        {
            var loadBinding = $"document.querySelector('#{propertyName}'){loadAccessor} = config.{propertyName};";
            var saveBinding = $"config.{propertyName} = document.querySelector('#{propertyName}'){saveAccessor};";

            Assert.AreEqual(2, CountOccurrences(html, $"config.{propertyName}"), $"{propertyName} 在配置页脚本中应只用于一次读取和一次保存绑定。");
            Assert.IsTrue(html.Contains(loadBinding, StringComparison.Ordinal), $"pageshow 时必须从 config.{propertyName} 回填字段。");
            Assert.IsTrue(html.Contains(saveBinding, StringComparison.Ordinal), $"submit 时必须把字段回写到 config.{propertyName}。");
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
