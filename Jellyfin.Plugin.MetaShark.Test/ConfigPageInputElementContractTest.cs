using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class ConfigPageInputElementContractTest
    {
        private static readonly string ConfigPagePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../Jellyfin.Plugin.MetaShark/Configuration/configPage.html"));

        [TestMethod]
        public void ManuallyLabeledInputs_ShouldNotUseLegacyEmbyInputCustomElement()
        {
            var html = File.ReadAllText(ConfigPagePath);
            var inputsUsingLegacyCustomElement = Regex.Matches(
                html,
                @"<input\b(?=[^>]*\bis=""emby-input"")[^>]*>",
                RegexOptions.Singleline);

            Assert.AreEqual(
                0,
                inputsUsingLegacyCustomElement.Count,
                "Jellyfin 10.10.7 的 input[is=\"emby-input\"] 会在插件页手写 label 结构下触发 htmlFor 控制台错误；手写 label 的 input 应使用普通 input + emby-input class。");
        }
    }
}
