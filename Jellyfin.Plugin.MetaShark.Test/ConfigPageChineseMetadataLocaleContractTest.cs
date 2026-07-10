using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class ConfigPageChineseMetadataLocaleContractTest
    {
        private const string PropertyName = "DefaultChineseMetadataLocale";
        private static readonly string ConfigPagePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../Jellyfin.Plugin.MetaShark/Configuration/configPage.html"));

        [TestMethod]
        public void ShouldExposeFourRegionalChineseChoicesAndRoundTripBinding()
        {
            var html = File.ReadAllText(ConfigPagePath);
            var select = Regex.Match(
                html,
                @"<select[^>]*id=""DefaultChineseMetadataLocale""[^>]*>(.*?)</select>",
                RegexOptions.Singleline);
            Assert.IsTrue(select.Success);
            var options = Regex.Matches(select.Value, @"<option\s+value=""([^""]+)"">\s*([^<]+?)\s*</option>");
            CollectionAssert.AreEqual(
                new[] { "zh-CN", "zh-SG", "zh-TW", "zh-HK" },
                options.Select(match => match.Groups[1].Value).ToArray());
            Assert.AreEqual(2, CountOccurrences(html, $"config.{PropertyName}"));
            Assert.IsTrue(html.Contains(
                "document.querySelector('#DefaultChineseMetadataLocale').value = config.DefaultChineseMetadataLocale;",
                StringComparison.Ordinal));
            Assert.IsTrue(html.Contains(
                "config.DefaultChineseMetadataLocale = document.querySelector('#DefaultChineseMetadataLocale').value;",
                StringComparison.Ordinal));
            Assert.IsTrue(html.Contains(
                "明确的 Jellyfin 中文地区语言优先；仅当语言为 zh 或无法确定地区时使用此默认值。只影响 TMDb 文本元数据，不改变 Douban 和图片语言。",
                StringComparison.Ordinal));
            Assert.IsTrue(html.IndexOf("id=\"DefaultChineseMetadataLocale\"", StringComparison.Ordinal)
                > html.IndexOf("<h3>TheMovieDb</h3>", StringComparison.Ordinal));
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            {
                count++;
            }

            return count;
        }
    }
}
