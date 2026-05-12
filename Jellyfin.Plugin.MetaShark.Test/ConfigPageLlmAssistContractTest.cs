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
            "EnableLlmTmdbIdCorrection",
            "EnableLlmTmdbCorrectionPersistence",
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
            AssertRoundTrip(html, "EnableLlmTmdbIdCorrection", ".checked");
            AssertRoundTrip(html, "EnableLlmTmdbCorrectionPersistence", ".checked");
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

            Assert.IsTrue(llmBlock.Contains("默认关闭，可选启用。开启后，只在手动识别、手动刷新或搜索缺失元数据等需要重新查询元数据的流程中按配置尝试调用；自动扫描、自动刷新、计划任务、媒体库扫描后任务和覆盖刷新不会触发；不会作为独立元数据源。", StringComparison.Ordinal), "LLM 辅助刮削必须说明默认关闭、触发范围、自动流程不触发和非独立元数据源。 ");
            Assert.IsTrue(llmBlock.Contains("外部 ID 辅助解析没有单独开关，复用此全局开关，不受文本补全开关控制，可把已有公开 ProviderIds（IMDb、TVDB、Douban、TMDb）作为上下文发送给 LLM。LLM 只能提出外部 ID 候选，也可能明确返回无候选；候选 ID 必须再经对应 API 或来源验证才会写入。只补写缺失的 ProviderIds，已有 ID 不会被覆盖。", StringComparison.Ordinal), "LLM 辅助刮削必须说明外部 ID 解析复用开关、无单独开关、公开 ProviderIds、空候选、API/source 验证、只写缺失项和不覆盖已有 ID。 ");
            Assert.IsTrue(llmBlock.Contains("默认关闭。仅允许纠正 Movie/Series 的 TMDb ID；只有在明显错误刮削时才会进入强制纠错评估，IMDb、TVDB、Douban 只作为证据或缺失补全，不做覆盖纠正。LLM 只能提出 TMDb 候选，候选必须经对应 API 或证据强验证；验证失败会保留原有 TMDb。启用本开关、全局 LLM 辅助刮削和完整 LLM 连接配置后，手动识别、手动刷新、搜索缺失元数据和显式覆盖刷新可按 TMDb 纠错触发器评估。自动扫描、隐式刷新、计划任务和其他自动流程不会触发；普通 LLM 辅助刮削、文本补全、外部 ID 缺失补全和剧集组映射不会因为覆盖刷新而运行。", StringComparison.Ordinal), "TMDb 纠错开关必须说明默认关闭、仅限 Movie/Series、明显错误刮削才进入强制纠错评估、其他外部 ID 只作证据或缺失补全、API 或证据强验证保留旧 TMDb、显式覆盖刷新可评估，以及 automatic/implicit/scheduled automatic blocked。 ");
            Assert.IsTrue(llmBlock.Contains("默认开启。仅在 LLM 已把 Movie/Series 的 Douban 明显误刮削强验证纠正为 TMDb 后，把 DoubanID 到 TMDb ID 的映射写入配置文件；后续刷新命中该记录时继续使用 TMDb 元数据，未命中的条目仍按默认刮削器策略处理。", StringComparison.Ordinal), "LLM Douban→TMDb 纠错持久化开关必须说明默认开启、只记录强验证纠错、写入配置文件、后续命中使用 TMDb、未命中保持默认策略。 ");
            Assert.IsTrue(llmBlock.Contains("OpenAI 兼容接口地址，需要填写到 /v1 级别；留空时不会调用 LLM。启用前请自行评估费用、隐私和网络风险。", StringComparison.Ordinal), "Base URL 必须说明 /v1 级别和费用、隐私、网络风险。");
            Assert.IsTrue(llmBlock.Contains("默认开启。仅发送相对媒体路径、公开 ProviderIds 和必要摘要，不发送完整本地路径、服务器 URL、Jellyfin 私有标识、API Key、cookie 或 token。关闭后，元数据、外部 ID 和剧集组映射提示都不会发送相对路径、文件名或目录结构。日志和证据不记录 prompt、API Key 或原始响应。", StringComparison.Ordinal), "相对路径开关必须说明发送边界和敏感信息排除范围。");
            Assert.IsTrue(llmBlock.Contains("默认关闭。全局 LLM 辅助刮削开关不等同于标题/简介文本补全；仅启用全局开关不会发送文本补全请求。开启本开关后，LLM 生成文本仅可在置信度和合并检查通过后回填空白或低风险的标题、简介类字段；不会覆盖权威元数据。外部 ID 辅助解析仍复用全局 LLM 开关独立运行。", StringComparison.Ordinal), "LLM 生成文本开关必须说明默认关闭、全局 LLM 开关不等同于文本补全、请求 gate、回填边界、不得覆盖权威元数据和外部 ID 独立运行。");
            Assert.IsTrue(llmBlock.Contains("允许范围 1-30 秒，默认 15 秒。超时、结构化输出不符合 schema 或并发限制忙碌时按可选能力关闭处理，不应变成 provider 错误。LLM 请求使用保守并发，同一时间默认只处理一个请求。", StringComparison.Ordinal), "超时说明必须包含默认 15 秒、1-30 范围、fail-closed 和保守并发。");
            Assert.IsTrue(llmBlock.Contains("默认 json-schema；不支持时可改为 json-object 或 text-json。结构化输出不符合 schema 时会跳过 LLM 结果，不写入候选内容。", StringComparison.Ordinal), "结构化输出说明必须包含 schema fail-closed 行为。");
            Assert.IsTrue(html.Contains("默认关闭。仅在手动识别、手动刷新或搜索缺失元数据触发时辅助判断，只能从 TMDb 返回的候选剧集组中选择。命中后会自动写进配置文件，写入成功后刷新受影响条目；不会写入 ProviderIds。关闭相对路径上下文后不会发送相对路径、文件名或目录结构，也不会发送完整本地路径、API Key、cookie 或 token。", StringComparison.Ordinal), "剧集组映射辅助必须说明默认关闭、触发范围、TMDb 候选限制、自动写回配置并刷新受影响条目，以及敏感信息排除范围。");
        }

        [TestMethod]
        public void ShouldDocumentExternalIdResolutionBoundariesOnConfigPage()
        {
            var llmBlock = GetLlmAssistBlock(ReadConfigPageHtml());

            AssertContainsAll(
                llmBlock,
                "配置页 LLM 分组必须说明外部 ID 解析的完整边界。",
                "外部 ID 辅助解析没有单独开关，复用此全局开关",
                "不受文本补全开关控制",
                "已有公开 ProviderIds（IMDb、TVDB、Douban、TMDb）作为上下文发送给 LLM",
                "也可能明确返回无候选",
                "候选 ID 必须再经对应 API 或来源验证才会写入",
                "只补写缺失的 ProviderIds",
                "已有 ID 不会被覆盖",
                "仅允许纠正 Movie/Series 的 TMDb ID",
                "IMDb、TVDB、Douban 只作为证据或缺失补全，不做覆盖纠正",
                "LLM 只能提出 TMDb 候选",
                "候选必须经对应 API 或证据强验证",
                "验证失败会保留原有 TMDb",
                "启用本开关、全局 LLM 辅助刮削和完整 LLM 连接配置后，手动识别、手动刷新、搜索缺失元数据和显式覆盖刷新可按 TMDb 纠错触发器评估",
                "自动扫描、隐式刷新、计划任务和其他自动流程不会触发",
                "普通 LLM 辅助刮削、文本补全、外部 ID 缺失补全和剧集组映射不会因为覆盖刷新而运行",
                "不发送完整本地路径、服务器 URL、Jellyfin 私有标识、API Key、cookie 或 token",
                "不会发送相对路径、文件名或目录结构",
                "不会作为独立元数据源",
                "自动扫描、自动刷新、计划任务、媒体库扫描后任务和覆盖刷新不会触发");
        }

        [TestMethod]
        public void Readme_ShouldDocumentLlmSafetyBoundaries()
        {
            var readme = File.ReadAllText(ReadmePath);

            Assert.IsTrue(readme.Contains("`LLM 辅助刮削`：默认关闭、可选启用，不是独立元数据源；只会在手动识别、手动刷新或搜索缺失元数据等需要重新查询元数据的流程中按配置尝试调用，自动扫描、自动刷新、计划任务、媒体库扫描后任务和覆盖刷新不会触发。", StringComparison.Ordinal), "README 必须说明 LLM 默认关闭、非独立元数据源、eligible 触发范围和自动流程不触发。 ");
            Assert.IsTrue(readme.Contains("LLM 外部 ID 辅助解析没有单独开关，复用 `LLM 辅助刮削` 全局开关和同一触发范围，不受文本补全开关控制；可把已有公开 ProviderIds（IMDb、TVDB、Douban、TMDb）作为上下文发送给 LLM。LLM 只能提出外部 ID 候选，也可能明确返回无候选；候选 ID 必须再经对应 API 或来源验证才会写入。只补写缺失的 ProviderIds，已有 ID 不会被覆盖。", StringComparison.Ordinal), "README 必须说明外部 ID 解析复用开关、无单独开关、公开 ProviderIds、空候选、API/source 验证、只写缺失项和不覆盖已有 ID。 ");
            Assert.IsTrue(readme.Contains("`允许 LLM 辅助纠正错误 TMDb ID`：默认关闭；只允许纠正 Movie/Series 的 TMDb ID。明显错误刮削可进入强制纠错评估，但 IMDb、TVDB、Douban 只作为证据或缺失补全，不做覆盖纠正。LLM 只能提出 TMDb 候选，候选必须再经对应 API 或证据强验证；验证失败会保留原有 TMDb。启用本开关、全局 `LLM 辅助刮削` 和完整 LLM 连接配置后，手动识别、手动刷新、搜索缺失元数据和显式覆盖刷新可按 TMDb 纠错触发器评估。自动扫描、隐式刷新、计划任务和其他自动流程不会触发；普通 LLM 辅助刮削、文本补全、外部 ID 缺失补全和剧集组映射不会因为覆盖刷新而运行。", StringComparison.Ordinal), "README 必须说明 TMDb 纠错开关默认关闭、仅限 Movie/Series、明显错误刮削可进入强制纠错评估、其他外部 ID 边界、API 或证据强验证保留旧 TMDb、显式覆盖刷新可评估，以及 automatic/implicit/scheduled automatic blocked。 ");
            Assert.IsTrue(readme.Contains("LLM 请求默认只发送相对媒体路径、公开 ProviderIds 和必要摘要，不发送完整本地路径、服务器 URL、Jellyfin 私有标识、API Key、cookie 或 token。关闭 `允许发送相对路径上下文` 后，元数据、外部 ID 和剧集组映射提示都不会发送相对路径、文件名或目录结构。", StringComparison.Ordinal), "README 必须说明 LLM 请求隐私边界和风险。 ");
            Assert.IsTrue(readme.Contains("LLM 元数据文本补全只在 `允许 LLM 生成文本回填` 开启时请求；关闭时不会发送标题、简介类补全请求。生成文本仅可在置信度和合并检查通过后回填空白或低风险字段，不覆盖权威元数据。", StringComparison.Ordinal), "README 必须说明 metadata text-completion 请求 gate。 ");
            Assert.IsTrue(readme.Contains("LLM 调用使用保守并发，同一时间默认只处理一个请求；超时、结构化输出不符合 schema、并发限制忙碌时会按可选能力关闭处理，不应变成 provider 错误。超时范围为 1-30 秒，默认 15 秒。", StringComparison.Ordinal), "README 必须说明保守并发、超时范围和 fail-closed 行为。 ");
            Assert.IsTrue(readme.Contains("日志和证据只记录状态、错误类别和字段名，不记录 prompt、API Key、原始响应、cookie、token 或完整敏感 URL；启用前请自行评估费用、隐私和网络风险。", StringComparison.Ordinal), "README 必须说明日志和证据脱敏边界。 ");
            Assert.IsTrue(readme.Contains("OpenAI 兼容 Base URL 需要填写到 `/v1` 级别", StringComparison.Ordinal), "README 必须说明 OpenAI 兼容 Base URL 填到 /v1 级别。 ");
            Assert.IsTrue(readme.Contains("`LLM 辅助建议 TMDb 剧集组映射`：默认关闭，只能从 TMDb 返回的候选剧集组中选择。命中后会自动写进配置文件，写入成功后刷新受影响条目；关闭相对路径上下文后不会发送路径样本。", StringComparison.Ordinal), "README 必须说明剧集组映射辅助默认关闭、只从 TMDb 候选中选择、命中后自动写回配置并刷新受影响条目。 ");
        }

        [TestMethod]
        public void Readme_ShouldDocumentExternalIdResolutionBoundaries()
        {
            var readme = File.ReadAllText(ReadmePath);

            AssertContainsAll(
                readme,
                "README 必须说明 LLM 外部 ID 解析的完整边界。",
                "LLM 外部 ID 辅助解析没有单独开关，复用 `LLM 辅助刮削` 全局开关和同一触发范围",
                "不受文本补全开关控制",
                "已有公开 ProviderIds（IMDb、TVDB、Douban、TMDb）作为上下文发送给 LLM",
                "也可能明确返回无候选",
                "候选 ID 必须再经对应 API 或来源验证才会写入",
                "只补写缺失的 ProviderIds",
                "已有 ID 不会被覆盖",
                "`允许 LLM 辅助纠正错误 TMDb ID`：默认关闭；只允许纠正 Movie/Series 的 TMDb ID",
                "明显错误刮削可进入强制纠错评估",
                "IMDb、TVDB、Douban 只作为证据或缺失补全，不做覆盖纠正",
                "LLM 只能提出 TMDb 候选",
                "候选必须再经对应 API 或证据强验证",
                "验证失败会保留原有 TMDb",
                "启用本开关、全局 `LLM 辅助刮削` 和完整 LLM 连接配置后，手动识别、手动刷新、搜索缺失元数据和显式覆盖刷新可按 TMDb 纠错触发器评估",
                "自动扫描、隐式刷新、计划任务和其他自动流程不会触发",
                "普通 LLM 辅助刮削、文本补全、外部 ID 缺失补全和剧集组映射不会因为覆盖刷新而运行",
                "不发送完整本地路径、服务器 URL、Jellyfin 私有标识、API Key、cookie 或 token",
                "不会发送相对路径、文件名或目录结构",
                "不是独立元数据源",
                "自动扫描、自动刷新、计划任务、媒体库扫描后任务和覆盖刷新不会触发",
                "`LLM 辅助建议 TMDb 剧集组映射`：默认关闭，只能从 TMDb 返回的候选剧集组中选择。命中后会自动写进配置文件，写入成功后刷新受影响条目；关闭相对路径上下文后不会发送路径样本。");
        }

        [TestMethod]
        public void ShouldNotDocumentUnimplementedExternalIdResolutionSwitchOrMisleadingProviderIdCopy()
        {
            var html = ReadConfigPageHtml();
            var readme = File.ReadAllText(ReadmePath);
            var combined = html + "\n" + readme;

            Assert.IsFalse(combined.Contains("EnableLlmExternalIdResolution", StringComparison.Ordinal), "文档和配置页不得出现未实现的独立外部 ID 开关。 ");
            Assert.IsFalse(combined.Contains("LLM 不写 ProviderIds", StringComparison.Ordinal), "文档不得继续声称 LLM 外部 ID 流程不写 ProviderIds。 ");
            Assert.IsFalse(combined.Contains("永不写 ProviderIds", StringComparison.Ordinal), "文档不得继续声称 LLM 外部 ID 流程永不写 ProviderIds。 ");
            Assert.IsFalse(combined.Contains("EnableLlmExternalIdResolution"), "不得把 TMDb 纠错开关误写成通用 external-id 开关。 ");
            Assert.IsFalse(combined.Contains("自动修复所有 ID", StringComparison.Ordinal), "文档不得声称 LLM 自动修复所有 ID。 ");
            Assert.IsFalse(combined.Contains("纠正所有 ProviderIds", StringComparison.Ordinal), "文档不得声称 LLM 纠正所有 ProviderIds。 ");
            Assert.IsFalse(combined.Contains("纠正 Episode", StringComparison.Ordinal), "文档不得声称 LLM 纠正 Episode TMDb。 ");
            Assert.IsFalse(combined.Contains("纠正 Season", StringComparison.Ordinal), "文档不得声称 LLM 纠正 Season TMDb。 ");
            Assert.IsFalse(combined.Contains("Episode 父级", StringComparison.Ordinal), "文档不得声称 LLM 纠正 Episode 父级 Series TMDb。 ");
            Assert.IsFalse(combined.Contains("原始 prompt", StringComparison.Ordinal), "文档不得暴露 raw prompt。 ");
            Assert.IsFalse(combined.Contains("原始 response", StringComparison.Ordinal), "文档不得暴露 raw response。 ");
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

        private static void AssertContainsAll(string text, string message, params string[] values)
        {
            foreach (var value in values)
            {
                Assert.IsTrue(text.Contains(value, StringComparison.Ordinal), $"{message} 缺少：{value}");
            }
        }
    }
}
