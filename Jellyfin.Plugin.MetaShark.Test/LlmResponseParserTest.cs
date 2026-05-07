using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmResponseParserTest
    {
        [TestMethod]
        public void Parse_WhenJsonSchemaResponseIsValid_ShouldReturnContentJson()
        {
            var result = LlmResponseParser.Parse(
                LlmApiTest.CreateSuccessEnvelope("{\"title\":\"三体\",\"confidence\":0.91}"),
                PluginConfiguration.LlmStructuredOutputModeJsonSchema);

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual("{\"title\":\"三体\",\"confidence\":0.91}", result.ContentJson);
        }

        [TestMethod]
        public void Parse_WhenJsonObjectResponseIsValid_ShouldReturnContentJson()
        {
            var result = LlmResponseParser.Parse(
                LlmApiTest.CreateSuccessEnvelope("{\"title\":\"三体\",\"confidence\":0.9}"),
                PluginConfiguration.LlmStructuredOutputModeJsonObject);

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual("{\"title\":\"三体\",\"confidence\":0.9}", result.ContentJson);
        }

        [TestMethod]
        public void Parse_WhenTextJsonResponseContainsJsonObject_ShouldExtractContentJson()
        {
            var result = LlmResponseParser.Parse(
                LlmApiTest.CreateSuccessEnvelope("输出如下：\n{\"title\":\"三体\",\"confidence\":0.9}\n请使用该 JSON。"),
                PluginConfiguration.LlmStructuredOutputModeTextJson);

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual("{\"title\":\"三体\",\"confidence\":0.9}", result.ContentJson);
        }

        [TestMethod]
        public void Parse_WhenChoicesAreEmpty_ShouldReturnFailureDiagnostic()
        {
            var result = LlmResponseParser.Parse("{\"choices\":[]}", PluginConfiguration.LlmStructuredOutputModeJsonSchema);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("choices", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void Parse_WhenContentIsEmpty_ShouldReturnFailureDiagnostic()
        {
            var result = LlmResponseParser.Parse("{\"choices\":[{\"message\":{\"content\":\"   \"},\"finish_reason\":\"stop\"}]}", PluginConfiguration.LlmStructuredOutputModeJsonSchema);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("content", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void Parse_WhenMessageRefusalExists_ShouldReturnFailureDiagnostic()
        {
            var result = LlmResponseParser.Parse("{\"choices\":[{\"message\":{\"refusal\":\"cannot comply\",\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}", PluginConfiguration.LlmStructuredOutputModeJsonSchema);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("refusal", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void Parse_WhenFinishReasonIsLength_ShouldReturnFailureDiagnostic()
        {
            var result = LlmResponseParser.Parse("{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"length\"}]}", PluginConfiguration.LlmStructuredOutputModeJsonSchema);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("length", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void Parse_WhenFinishReasonIsContentFilter_ShouldReturnFailureDiagnostic()
        {
            var result = LlmResponseParser.Parse("{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"content_filter\"}]}", PluginConfiguration.LlmStructuredOutputModeJsonSchema);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("content_filter", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void Parse_WhenEnvelopeJsonIsInvalid_ShouldReturnFailureDiagnostic()
        {
            var result = LlmResponseParser.Parse("not json", PluginConfiguration.LlmStructuredOutputModeJsonSchema);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("invalid JSON", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void Parse_WhenContentSchemaIsInvalid_ShouldReturnFailureDiagnostic()
        {
            var result = LlmResponseParser.Parse(
                LlmApiTest.CreateSuccessEnvelope("[\"not\",\"an\",\"object\"]"),
                PluginConfiguration.LlmStructuredOutputModeJsonSchema);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("schema", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void Parse_WhenErrorEnvelopeExists_ShouldReturnFailureDiagnostic()
        {
            var result = LlmResponseParser.Parse("{\"error\":{\"message\":\"bad request\",\"type\":\"invalid_request_error\",\"param\":\"messages\",\"code\":\"bad_json\"}}", PluginConfiguration.LlmStructuredOutputModeJsonSchema);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("bad request", StringComparison.Ordinal), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("invalid_request_error", StringComparison.Ordinal), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("messages", StringComparison.Ordinal), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("bad_json", StringComparison.Ordinal), result.Diagnostic);
        }
    }
}
