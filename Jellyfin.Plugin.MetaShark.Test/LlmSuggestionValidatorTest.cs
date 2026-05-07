using Jellyfin.Plugin.MetaShark.Providers.Llm;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmSuggestionValidatorTest
    {
        [TestMethod]
        public void ParseAndValidate_RejectsTooLongFields()
        {
            var json = "{\"title\":\"" + new string('a', 201) + "\",\"confidence\":0.9}";

            var result = Validate(json);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("too long", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow(1800)]
        [DataRow(3000)]
        public void ParseAndValidate_RejectsInvalidYear(int year)
        {
            var result = Validate($"{{\"title\":\"三体\",\"year\":{year},\"confidence\":0.9}}");

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("year", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidate_RejectsEmptySuggestion()
        {
            var result = Validate("{\"confidence\":0.9}");

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("empty", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidate_RejectsUnknownMediaType()
        {
            var result = Validate("{\"mediaType\":\"Person\",\"title\":\"某人\",\"confidence\":0.9}");

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("media type", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow("[]")]
        [DataRow("{\"title\":123,\"confidence\":0.9}")]
        [DataRow("{\"title\":\"三体\",\"ProviderIds\":{\"Tmdb\":\"1\"},\"confidence\":0.9}")]
        [DataRow("{\"title\":\"三体\",\"tagline\":\"标语\",\"confidence\":0.9}")]
        public void ParseAndValidate_RejectsInvalidSchema(string json)
        {
            var result = Validate(json);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("schema", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow(-0.1)]
        [DataRow(1.1)]
        public void ParseAndValidate_RejectsConfidenceOutOfRange(double confidence)
        {
            var result = Validate($"{{\"title\":\"三体\",\"confidence\":{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("confidence", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidate_AcceptsValidSuggestionAndTrimsText()
        {
            var result = Validate("{\"mediaType\":\"Movie\",\"title\":\"  三体  \",\"year\":2023,\"confidence\":0.9}");

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual("三体", result.Suggestion!.Title);
            Assert.AreEqual(2023, result.Suggestion.Year);
        }

        private static LlmSuggestionValidationResult Validate(string json)
        {
            return new LlmSuggestionValidator().ParseAndValidate(json, 0.75);
        }
    }
}
