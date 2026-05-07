using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MetaShark.Providers.Llm;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmExternalIdCandidateValidatorTest
    {
        [DataTestMethod]
        [DataRow("TMDb", "27205", "Movie")]
        [DataRow("TMDb", "1396", "Series")]
        [DataRow("TVDB", "81189", "Series")]
        [DataRow("IMDb", "tt1375666", "Movie")]
        [DataRow("Douban", "3541415", "Movie")]
        [DataRow("TMDb", "9876", "Episode")]
        [DataRow("TVDB", "12345", "Episode")]
        public void ParseAndValidateResponse_AcceptsSingleBareCandidateObjectForCompatibility(string provider, string id, string mediaType)
        {
            var result = Validate(CandidateJson(provider, id, mediaType));

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual(provider, result.Candidate!.Provider);
            Assert.AreEqual(id, result.Candidate.Id);
            Assert.AreEqual(mediaType, result.Candidate.MediaType);
        }

        [TestMethod]
        public void ParseAndValidateResponse_AcceptsCanonicalExternalIdCandidatesBatch()
        {
            var result = Validate(ResponseJson(CandidateJson("TMDb", "27205", "Movie"), CandidateJson("IMDb", "tt1375666", "Movie")));

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual(2, result.Candidates.Count);
            CollectionAssert.AreEqual(new[] { "TMDb", "IMDb" }, result.Candidates.Select(candidate => candidate.Provider).ToArray());
        }

        [TestMethod]
        public void ParseAndValidateResponse_AcceptsCanonicalEmptyExternalIdCandidatesBatch()
        {
            var result = Validate(@"{""externalIdCandidates"":[]}");

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual(0, result.Candidates.Count);
            Assert.IsNull(result.Candidate);
            Assert.IsTrue(result.Diagnostic.Contains("no candidates", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidateResponse_NormalizesImdbIdCaseAndTrimsText()
        {
            var result = Validate(@"{""provider"":"" imdb "",""id"":"" TT1375666 "",""mediaType"":"" movie "",""confidence"":0.91,""reason"":"" exact title "",""evidence"":"" filename match ""}");

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual("IMDb", result.Candidate!.Provider);
            Assert.AreEqual("tt1375666", result.Candidate.Id);
            Assert.AreEqual("Movie", result.Candidate.MediaType);
            Assert.AreEqual("exact title", result.Candidate.Reason);
            Assert.AreEqual("filename match", result.Candidate.Evidence);
        }

        [DataTestMethod]
        [DataRow("TVDB", "Movie")]
        [DataRow("TVDB", "Season")]
        [DataRow("TMDb", "Season")]
        [DataRow("Douban", "Episode")]
        [DataRow("IMDb", "Episode")]
        public void ParseAndValidateResponse_RejectsIllegalProviderMediaTypeMatrix(string provider, string mediaType)
        {
            var result = Validate(CandidateJson(provider, ProviderDefaultId(provider), mediaType));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("provider", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("media type", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidateResponse_RejectsUnknownMediaType()
        {
            var result = Validate(CandidateJson("TMDb", "27205", "Person"));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("media type", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow("TMDb", "0")]
        [DataRow("TMDb", "-1")]
        [DataRow("TMDb", "abc")]
        [DataRow("TVDB", "0")]
        [DataRow("Douban", "12.3")]
        [DataRow("IMDb", "1375666")]
        [DataRow("IMDb", "tt123456")]
        [DataRow("IMDb", "nm0000138")]
        public void ParseAndValidateResponse_RejectsIllegalIdFormat(string provider, string id)
        {
            var result = Validate(CandidateJson(provider, id, ProviderDefaultMediaType(provider)));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("id format", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow("provider")]
        [DataRow("id")]
        [DataRow("mediaType")]
        [DataRow("confidence")]
        [DataRow("reason")]
        [DataRow("evidence")]
        public void ParseAndValidateResponse_RejectsMissingRequiredFields(string omittedField)
        {
            var result = Validate(CandidateJson(omitField: omittedField));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("required", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains(omittedField, StringComparison.Ordinal), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow("provider", "   ")]
        [DataRow("id", "   ")]
        [DataRow("mediaType", "   ")]
        [DataRow("reason", "   ")]
        [DataRow("evidence", "   ")]
        public void ParseAndValidateResponse_RejectsBlankRequiredStringFields(string fieldName, string value)
        {
            var result = Validate(CandidateJson(replaceField: fieldName, replacementValue: value));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains(fieldName == "id" ? "id" : fieldName.Replace("mediaType", "media type", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow("[]")]
        [DataRow(@"{""provider"":""TMDb"",""id"":""27205"",""mediaType"":""Movie"",""confidence"":0.9,""reason"":""match"",""evidence"":""file"",""providerIds"":{""Tmdb"":""27205""}}")]
        [DataRow(@"{""provider"":""TMDb"",""id"":""27205"",""mediaType"":""Movie"",""confidence"":0.9,""reason"":""match"",""evidence"":""file"",""ProviderIds"":{""Tmdb"":""27205""}}")]
        [DataRow(@"{""externalIdCandidates"":[{""provider"":""TMDb"",""id"":""27205"",""mediaType"":""Movie"",""confidence"":0.9,""reason"":""match"",""evidence"":""file""}],""providerIds"":{""Tmdb"":""27205""}}")]
        [DataRow(@"{""externalIdCandidates"":[{""provider"":""TMDb"",""id"":""27205"",""mediaType"":""Movie"",""confidence"":0.9,""reason"":""match"",""evidence"":""file""}],""ProviderIds"":{""Tmdb"":""27205""}}")]
        public void ParseAndValidateResponse_RejectsInvalidSchemaAndExtraFields(string json)
        {
            var result = Validate(json);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("schema", StringComparison.OrdinalIgnoreCase) || result.Diagnostic.Contains("no candidates", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidateResponse_RejectsLegacyCandidatesTopLevelField()
        {
            var result = Validate(@"{""candidates"":[{""provider"":""TMDb"",""id"":""27205"",""mediaType"":""Movie"",""confidence"":0.9,""reason"":""match"",""evidence"":""file""}]}");

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("candidates", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("not allowed", StringComparison.OrdinalIgnoreCase) || result.Diagnostic.Contains("required", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow(-0.1)]
        [DataRow(1.1)]
        public void ParseAndValidateResponse_RejectsConfidenceOutOfRange(double confidence)
        {
            var result = Validate(CandidateJson(confidence: confidence));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("confidence", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidateResponse_RejectsLowConfidence()
        {
            var result = Validate(CandidateJson(confidence: 0.74));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("threshold", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidateResponse_RejectsTooLongFields()
        {
            var result = Validate(CandidateJson(evidence: new string('a', 1001)));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostic.Contains("too long", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ParseAndValidateResponse_WhenBatchHasPartialValidity_ReturnsAcceptedCandidatesAndDiagnostics()
        {
            var json = ResponseJson(CandidateJson("TMDb", "27205", "Movie"), CandidateJson("TVDB", "81189", "Movie"), CandidateJson("IMDb", "TT1375666", "Movie"));

            var result = Validate(json);

            Assert.IsTrue(result.Success, result.Diagnostic);
            Assert.AreEqual(2, result.Candidates.Count);
            CollectionAssert.AreEqual(new[] { "TMDb", "IMDb" }, result.Candidates.Select(candidate => candidate.Provider).ToArray());
            Assert.AreEqual("tt1375666", result.Candidates[1].Id);
            Assert.IsTrue(result.Diagnostics.Count > 0);
            Assert.IsTrue(result.Diagnostic.Contains("media type", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public void ResolutionResult_FactoryMethodsExposeBoundaryStatuses()
        {
            var candidate = new LlmExternalIdCandidate { Provider = "TMDb", Id = "27205", MediaType = "Movie", Confidence = 0.9, Reason = "match", Evidence = "file" };

            Assert.AreEqual(LlmExternalIdResolutionStatus.NotTriggered, LlmExternalIdResolutionResult.NotTriggered("off").Status);
            Assert.AreEqual(LlmExternalIdResolutionStatus.Skipped, LlmExternalIdResolutionResult.Skipped("existing id").Status);
            Assert.AreEqual(LlmExternalIdResolutionStatus.Rejected, LlmExternalIdResolutionResult.Rejected("unsupported").Status);
            Assert.AreEqual(LlmExternalIdResolutionStatus.ValidationFailed, LlmExternalIdResolutionResult.ValidationFailed("bad json").Status);
            Assert.AreEqual(LlmExternalIdResolutionStatus.VerificationFailed, LlmExternalIdResolutionResult.VerificationFailed("not found", candidate).Status);
            Assert.AreEqual(LlmExternalIdResolutionStatus.Succeeded, LlmExternalIdResolutionResult.Succeeded(candidate).Status);
            Assert.IsTrue(LlmExternalIdResolutionResult.Succeeded(candidate).Success);
        }

        private static LlmExternalIdCandidateValidationResult Validate(string json)
        {
            return new LlmExternalIdCandidateValidator().ParseAndValidateResponse(json, 0.75);
        }

        private static string ProviderDefaultId(string provider)
        {
            return string.Equals(provider, "IMDb", StringComparison.OrdinalIgnoreCase) ? "tt1375666" : "27205";
        }

        private static string ProviderDefaultMediaType(string provider)
        {
            return string.Equals(provider, "TVDB", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie";
        }

        private static string ResponseJson(params string[] candidates)
        {
            return "{\"externalIdCandidates\":[" + string.Join(",", candidates) + "]}";
        }

        private static string CandidateJson(
            string provider = "TMDb",
            string id = "27205",
            string mediaType = "Movie",
            double confidence = 0.9,
            string reason = "title and year match",
            string evidence = "filename contains Inception 2010",
            string? omitField = null,
            string? replaceField = null,
            string? replacementValue = null)
        {
            var fields = new Dictionary<string, string>
            {
                ["provider"] = JsonString(provider),
                ["id"] = JsonString(id),
                ["mediaType"] = JsonString(mediaType),
                ["confidence"] = confidence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reason"] = JsonString(reason),
                ["evidence"] = JsonString(evidence),
            };

            if (omitField != null)
            {
                fields.Remove(omitField);
            }

            if (replaceField != null)
            {
                fields[replaceField] = JsonString(replacementValue ?? string.Empty);
            }

            return "{" + string.Join(",", fields.Select(field => "\"" + field.Key + "\":" + field.Value)) + "}";
        }

        private static string JsonString(string value)
        {
            return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }
    }
}
