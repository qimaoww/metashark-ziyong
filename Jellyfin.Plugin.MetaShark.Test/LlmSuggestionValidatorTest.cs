using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using Microsoft.Extensions.Logging;
using Moq;

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
        [DataRow("{\"suggestions\":123}")]
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
        public void ParseAndValidate_RejectsInvalidConfidenceType()
        {
            var result = Validate("{\"title\":\"三体\",\"confidence\":\"0.9\"}");

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("confidence", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("number", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow("sortTitle")]
        [DataRow("SortTitle")]
        [DataRow("SortName")]
        public void ParseAndValidate_IgnoresUnknownSuggestionFields(string unknownField)
        {
            const string unknownValue = "不应使用";
            var loggerStub = new Mock<ILogger<LlmSuggestionValidator>>();
            loggerStub.Setup(logger => logger.IsEnabled(LogLevel.Debug)).Returns(true);

            var result = Validate($"{{\"suggestions\":[{{\"title\":\"三体\",\"{unknownField}\":\"{unknownValue}\",\"confidence\":0.9}}]}}", loggerStub.Object);

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual("三体", result.Suggestion!.Title);
            Assert.IsFalse(typeof(LlmScrapingSuggestion).GetProperties().Any(property => string.Equals(property.Name, unknownField, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(result.Diagnostic.Contains(unknownValue, StringComparison.Ordinal), result.Diagnostic);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?> { ["FieldName"] = unknownField },
                originalFormatContains: "ignored unknown field",
                unknownField);
            Assert.IsFalse(loggerStub.Invocations.Any(invocation => invocation.Arguments.Any(argument => argument?.ToString()?.Contains(unknownValue, StringComparison.Ordinal) == true)));
        }

        [DataTestMethod]
        [DataRow("sortTitle")]
        [DataRow("providerIds")]
        public void ParseAndValidate_RejectsUnknownTopLevelFieldsWhenSuggestionsEnvelopeExists(string unknownField)
        {
            var result = Validate($"{{\"suggestions\":[],\"{unknownField}\":{{}}}}");

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("schema", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains(unknownField, StringComparison.Ordinal), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("not allowed", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidate_WhenTopLevelSuggestionsHasMissingConfidence_ShouldReturnNoCandidate()
        {
            var result = Validate("{\"suggestions\":[{\"title\":\"三体\"}]}");

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.IsNull(result.Suggestion);
            Assert.AreEqual("NoCandidate", result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidate_WhenDirectSuggestionHasMissingConfidence_ShouldRejectSuggestion()
        {
            var result = Validate("{\"title\":\"三体\"}");

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("confidence", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidate_WhenTopLevelSuggestionsEmpty_ShouldReturnNoCandidate()
        {
            var result = Validate("{\"suggestions\":[]}");

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.IsNull(result.Suggestion);
            Assert.AreEqual("NoCandidate", result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidate_WhenFirstSuggestionInvalid_ShouldUseNextValidSuggestion()
        {
            var result = Validate("{\"suggestions\":[{\"title\":\"缺 confidence\"},{\"title\":\"三体\",\"confidence\":0.9}]}");

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual("三体", result.Suggestion!.Title);
            Assert.AreEqual(0.9, result.Suggestion.Confidence);
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

        private static LlmSuggestionValidationResult Validate(string json, ILogger<LlmSuggestionValidator> logger)
        {
            return new LlmSuggestionValidator(logger).ParseAndValidate(json, 0.75);
        }
    }
}
