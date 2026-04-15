using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class ConfigPageCheckboxNormalizationContractTest
    {
        private const string UnifiedSelector = "#TemplateConfigPage .checkboxContainer input[type=\"checkbox\"][is=\"emby-checkbox\"]";

        private static readonly string ConfigPagePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../Jellyfin.Plugin.MetaShark/Configuration/configPage.html"));

        [TestMethod]
        public void ShouldDefineExactlyOneNormalizeHelperAndRemoveLegacyTvdbHelper()
        {
            var html = ReadConfigPageHtml();

            Assert.AreEqual(
                1,
                Regex.Matches(html, @"function\s+normalizeConfigPageCheckboxes\s*\(\s*\)").Count,
                "configPage.html 必须且仅允许定义一个 normalizeConfigPageCheckboxes() helper。");
            Assert.IsFalse(
                html.Contains("ensureTvdbSpecialsWithinSeasonsCheckbox("),
                "configPage.html 不得再保留 TVDB-only checkbox helper 或其调用点。");
        }

        [TestMethod]
        public void ShouldUseSingleUnifiedCheckboxSelectorWithoutCheckboxSpecificBranches()
        {
            var html = ReadConfigPageHtml();
            var helperBody = GetNormalizeHelperBody(html);

            Assert.AreEqual(
                1,
                CountOccurrences(html, UnifiedSelector),
                "configPage.html 必须且仅允许出现一个统一 checkbox selector。" );
            Assert.IsTrue(helperBody.Contains(UnifiedSelector, StringComparison.Ordinal), "统一 helper 必须使用统一 checkbox selector。" );
            Assert.IsFalse(helperBody.Contains("EnableTvdbSpecialsWithinSeasons"), "统一 helper 不应包含 TVDB-only 分支。");
            Assert.IsFalse(helperBody.Contains("EnableSearchMissingMetadataEpisodeTitleBackfill"), "统一 helper 不应包含标题回填 checkbox 特例分支。");
        }

        [TestMethod]
        public void ShouldEnhanceExpectedCheckboxNodesWithIdempotentGuard()
        {
            var helperBody = GetNormalizeHelperBody(ReadConfigPageHtml());
            var existingOutlineGuardIndex = GetRequiredIndex(
                helperBody,
                "if (label.querySelector('.checkboxOutline'))",
                "统一 helper 必须在已有 checkboxOutline 时直接跳过，避免重复插入 outline。");
            var existingOutlineContinueIndex = helperBody.IndexOf("continue;", existingOutlineGuardIndex, StringComparison.Ordinal);
            var outlineSourceIndex = GetRequiredIndex(
                helperBody,
                "var outlineSource =",
                "统一 helper 必须先确认 outlineSource，再执行 checkbox 增强写入。");
            var missingOutlineSourceGuardIndex = GetRequiredIndex(
                helperBody,
                "if (!outlineSource)",
                "统一 helper 必须在 outlineSource 不可用时直接跳过，避免留下半增强 checkbox。");
            var labelWriteIndex = GetRequiredIndex(
                helperBody,
                "labelText.classList.add('checkboxLabel')",
                "统一 helper 必须补 checkboxLabel。" );
            var dataMarkerWriteIndex = GetRequiredIndex(
                helperBody,
                "checkbox.setAttribute('data-embycheckbox', 'true')",
                "统一 helper 必须写入 data-embycheckbox 标记。" );
            var embyClassWriteIndex = GetRequiredIndex(
                helperBody,
                "checkbox.classList.add('emby-checkbox')",
                "统一 helper 必须补 emby-checkbox class。" );

            Assert.IsTrue(helperBody.Contains(".checkboxOutline", StringComparison.Ordinal), "统一 helper 必须处理 checkboxOutline。" );
            Assert.IsTrue(existingOutlineContinueIndex > existingOutlineGuardIndex, "统一 helper 必须在已有 checkboxOutline 时直接跳过，避免重复插入 outline。" );
            Assert.IsTrue(outlineSourceIndex < missingOutlineSourceGuardIndex, "outlineSource 检查必须先于增强写入 guard。" );
            Assert.IsTrue(missingOutlineSourceGuardIndex < labelWriteIndex, "必须先确认 outlineSource 可用，再补 checkboxLabel。" );
            Assert.IsTrue(missingOutlineSourceGuardIndex < dataMarkerWriteIndex, "必须先确认 outlineSource 可用，再写入 data-embycheckbox。" );
            Assert.IsTrue(missingOutlineSourceGuardIndex < embyClassWriteIndex, "必须先确认 outlineSource 可用，再补 emby-checkbox class。" );
        }

        [TestMethod]
        public void ShouldNormalizeOnPageShowAndOnlyUseSingleDeferredRetryStrategy()
        {
            var html = ReadConfigPageHtml();

            Assert.IsTrue(html.Contains(".addEventListener('pageshow'", StringComparison.Ordinal), "configPage.html 必须监听 pageshow 生命周期。" );
            Assert.AreEqual(1, CountOccurrences(html, "normalizeConfigPageCheckboxes();"), "pageshow 成功加载后必须直接调用一次 normalize helper。");
            Assert.IsTrue(
                Regex.IsMatch(html, @"requestAnimationFrame\(\s*normalizeConfigPageCheckboxes\s*\)")
                || Regex.IsMatch(html, @"setTimeout\(\s*normalizeConfigPageCheckboxes\s*,\s*0\s*\)"),
                "pageshow 后必须通过 requestAnimationFrame 或单次 setTimeout(..., 0) 再补一次 normalize。" );
            Assert.IsFalse(Regex.IsMatch(html, @"setInterval\s*\("), "checkbox normalize 不允许使用 interval。" );
            Assert.IsFalse(Regex.IsMatch(html, @"MutationObserver"), "checkbox normalize 不允许使用 MutationObserver。" );
        }

        private static string ReadConfigPageHtml()
        {
            return File.ReadAllText(ConfigPagePath);
        }

        private static string GetNormalizeHelperBody(string html)
        {
            var signature = "function normalizeConfigPageCheckboxes()";
            var signatureIndex = html.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(signatureIndex >= 0, "configPage.html 缺少 normalizeConfigPageCheckboxes() helper。");

            var bodyStartIndex = html.IndexOf('{', signatureIndex + signature.Length);
            Assert.IsTrue(bodyStartIndex >= 0, "normalizeConfigPageCheckboxes() helper 缺少函数体。");

            var braceDepth = 0;
            for (var index = bodyStartIndex; index < html.Length; index++)
            {
                if (html[index] == '{')
                {
                    braceDepth++;
                }
                else if (html[index] == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        return html.Substring(bodyStartIndex + 1, index - bodyStartIndex - 1);
                    }
                }
            }

            Assert.Fail("normalizeConfigPageCheckboxes() helper 的函数体没有正确闭合。");
            return string.Empty;
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

        private static int GetRequiredIndex(string text, string value, string message)
        {
            var index = text.IndexOf(value, StringComparison.Ordinal);
            Assert.IsTrue(index >= 0, message);
            return index;
        }
    }
}
