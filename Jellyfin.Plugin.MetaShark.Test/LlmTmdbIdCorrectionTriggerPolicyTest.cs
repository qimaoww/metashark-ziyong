using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmTmdbIdCorrectionTriggerPolicyTest
    {
        [DataTestMethod]
        [DataRow("Movie")]
        [DataRow("Series")]
        public void Evaluate_AllowsExplicitManualMatchForMovieAndSeries(string mediaType)
        {
            var decision = Evaluate(DefaultScraperSemantic.ManualMatch, mediaType, CreateManualApplyContext());

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ManualMatchReason, decision.Reason);
        }

        [DataTestMethod]
        [DataRow("Movie")]
        [DataRow("Series")]
        public void Evaluate_AllowsExplicitUserRefreshForMovieAndSeries(string mediaType)
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, mediaType, CreateRefreshContext("?metadataRefreshMode=RefreshMetadata&replaceAllMetadata=false"));

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ExplicitUserRefreshReason, decision.Reason);
        }

        [DataTestMethod]
        [DataRow("Movie")]
        [DataRow("Series")]
        public void Evaluate_AllowsExplicitSearchMissingRefreshForMovieAndSeries(string mediaType)
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, mediaType, CreateRefreshContext("?metadataRefreshMode=FullRefresh&replaceAllMetadata=false"));

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ExplicitSearchMissingMetadataRefreshReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_AllowsExplicitSearchMissingRefreshWithUpperCaseReplaceAllMetadataKey()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Movie", CreateRefreshContext("?metadataRefreshMode=FullRefresh&ReplaceAllMetadata=false"));

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ExplicitSearchMissingMetadataRefreshReason, decision.Reason);
        }

        [DataTestMethod]
        [DataRow("Movie")]
        [DataRow("Series")]
        public void Evaluate_AllowsExplicitOverwriteRefreshForMovieAndSeries(string mediaType)
        {
            var decision = Evaluate(DefaultScraperSemantic.OverwriteRefresh, mediaType, CreateRefreshContext("?metadataRefreshMode=FullRefresh&replaceAllMetadata=true"));

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ExplicitOverwriteRefreshReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_AllowsOverwriteRouteWhenSemanticHasNotBeenClassifiedYet()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Series", CreateRefreshContext("?metadataRefreshMode=RefreshMetadata&ReplaceAllMetadata=true"));

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ExplicitOverwriteRefreshReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsAutomaticRefreshEvenWithExplicitSearchMissingQuery()
        {
            var decision = Evaluate(DefaultScraperSemantic.AutomaticRefresh, "Movie", CreateRefreshContext("?metadataRefreshMode=FullRefresh&replaceAllMetadata=false"));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.AutomaticRefreshRejectedReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsUserRefreshWithoutHttpContext()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Movie", null);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ImplicitRefreshRejectedReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsExplicitRefreshRouteWithoutUserIntentQuery()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Series", CreateRefreshContext(string.Empty));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ImplicitRefreshRejectedReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsScheduledAutomaticRefreshWithoutHttpProof()
        {
            var decision = Evaluate(DefaultScraperSemantic.AutomaticRefresh, "Series", null);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.AutomaticRefreshRejectedReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsImageProvider()
        {
            var decision = new LlmTmdbIdCorrectionTriggerPolicy().Evaluate(CreateContext(DefaultScraperSemantic.ManualMatch, "Movie", CreateManualApplyContext(), isImageProvider: true));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ImageProviderRejectedReason, decision.Reason);
        }

        [DataTestMethod]
        [DataRow("Episode")]
        [DataRow("Season")]
        [DataRow("Person")]
        public void Evaluate_RejectsUnsupportedMediaTypes(string mediaType)
        {
            var decision = Evaluate(DefaultScraperSemantic.ManualMatch, mediaType, CreateManualApplyContext());

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.UnsupportedMediaTypeReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsManualMatchWithoutManualApplyRoute()
        {
            var decision = Evaluate(DefaultScraperSemantic.ManualMatch, "Movie", CreateRefreshContext("?metadataRefreshMode=RefreshMetadata&replaceAllMetadata=false"));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ImplicitRefreshRejectedReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsSearchMissingWithoutExplicitFalseReplaceAllMetadata()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Movie", CreateRefreshContext("?metadataRefreshMode=FullRefresh"));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ImplicitRefreshRejectedReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsMissingCorrectionConfiguration()
        {
            var context = CreateContext(DefaultScraperSemantic.ManualMatch, "Movie", CreateManualApplyContext());
            context.Configuration!.EnableLlmTmdbIdCorrection = false;

            var decision = new LlmTmdbIdCorrectionTriggerPolicy().Evaluate(context);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ConfigurationMissingReason, decision.Reason);
        }

        [DataTestMethod]
        [DataRow("LlmBaseUrl")]
        [DataRow("LlmModel")]
        [DataRow("LlmApiKey")]
        public void Evaluate_RejectsIncompleteLlmConnectionConfiguration(string missingProperty)
        {
            var context = CreateContext(DefaultScraperSemantic.ManualMatch, "Movie", CreateManualApplyContext());
            switch (missingProperty)
            {
                case "LlmBaseUrl":
                    context.Configuration!.LlmBaseUrl = string.Empty;
                    break;
                case "LlmModel":
                    context.Configuration!.LlmModel = string.Empty;
                    break;
                case "LlmApiKey":
                    context.Configuration!.LlmApiKey = string.Empty;
                    break;
            }

            var decision = new LlmTmdbIdCorrectionTriggerPolicy().Evaluate(context);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ConfigurationMissingReason, decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsWhenGeneralLlmAssistSwitchIsDisabled()
        {
            var context = CreateContext(DefaultScraperSemantic.ManualMatch, "Movie", CreateManualApplyContext());
            context.Configuration!.EnableLlmAssist = false;

            var decision = new LlmTmdbIdCorrectionTriggerPolicy().Evaluate(context);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ConfigurationMissingReason, decision.Reason);
        }

        [TestMethod]
        public void ExistingAssistTriggerPolicy_StillRejectsOverwriteRefresh()
        {
            var context = new LlmAssistTriggerContext
            {
                Configuration = CreateConfiguration(),
                Semantic = DefaultScraperSemantic.OverwriteRefresh,
                MediaType = "Movie",
                HttpContext = CreateRefreshContext("?metadataRefreshMode=FullRefresh&replaceAllMetadata=true"),
            };

            var decision = new LlmAssistTriggerPolicy().Evaluate(context);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("OverwriteRefreshRejected", decision.Reason);
        }

        private static LlmAssistTriggerDecision Evaluate(DefaultScraperSemantic semantic, string mediaType, HttpContext? httpContext)
        {
            return new LlmTmdbIdCorrectionTriggerPolicy().Evaluate(CreateContext(semantic, mediaType, httpContext));
        }

        private static LlmAssistTriggerContext CreateContext(DefaultScraperSemantic semantic, string mediaType, HttpContext? httpContext, bool isImageProvider = false)
        {
            return new LlmAssistTriggerContext
            {
                Configuration = CreateConfiguration(),
                Semantic = semantic,
                MediaType = mediaType,
                IsImageProvider = isImageProvider,
                HttpContext = httpContext,
            };
        }

        private static PluginConfiguration CreateConfiguration()
        {
            return new PluginConfiguration
            {
                EnableLlmAssist = true,
                EnableLlmTmdbIdCorrection = true,
                LlmBaseUrl = "http://127.0.0.1:11434/v1",
                LlmModel = "test-model",
                LlmApiKey = "test-key",
            };
        }

        private static DefaultHttpContext CreateManualApplyContext()
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/Items/RemoteSearch/Apply/11111111-1111-1111-1111-111111111111";
            return context;
        }

        private static DefaultHttpContext CreateRefreshContext(string queryString)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/Items/11111111-1111-1111-1111-111111111111/Refresh";
            context.Request.QueryString = new QueryString(queryString);
            return context;
        }
    }
}
