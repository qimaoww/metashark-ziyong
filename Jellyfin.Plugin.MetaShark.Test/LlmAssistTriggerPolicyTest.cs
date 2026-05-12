using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmAssistTriggerPolicyTest
    {
        [TestMethod]
        public void Evaluate_AllowsManualMatch()
        {
            var decision = Evaluate(DefaultScraperSemantic.ManualMatch, "Movie", CreateManualApplyContext());

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual("ManualMatch", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_AllowsExplicitUserRefresh()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Series", CreateRefreshContext("?metadataRefreshMode=RefreshMetadata&replaceAllMetadata=false"));

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual("ExplicitUserRefresh", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_AllowsExplicitSearchMissingRefresh()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Episode", CreateRefreshContext("?metadataRefreshMode=FullRefresh&replaceAllMetadata=false"));

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual("ExplicitSearchMissingMetadataRefresh", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_AllowsExplicitSearchMissingRefreshWithUpperCaseReplaceAllMetadataKey()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Movie", CreateRefreshContext("?metadataRefreshMode=FullRefresh&ReplaceAllMetadata=false"));

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual("ExplicitSearchMissingMetadataRefresh", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_AllowsBridgedExplicitSearchMissingRefreshWithoutQuery()
        {
            var context = CreateContext(DefaultScraperSemantic.UserRefresh, "Series", CreateRefreshContext(string.Empty));
            context.HasBridgedExplicitSearchMissingMetadataRefreshIntent = true;

            var decision = new LlmAssistTriggerPolicy().Evaluate(context);

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual("ExplicitSearchMissingMetadataRefresh", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsBridgedExplicitSearchMissingRefreshForImageProvider()
        {
            var context = CreateContext(DefaultScraperSemantic.UserRefresh, "Series", CreateRefreshContext(string.Empty), isImageProvider: true);
            context.HasBridgedExplicitSearchMissingMetadataRefreshIntent = true;

            var decision = new LlmAssistTriggerPolicy().Evaluate(context);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("ImageProviderRejected", decision.Reason);
        }

        [TestMethod]
        public void ShouldEvaluateDeterministicMismatch_OnlyWhenTriggerAllowed()
        {
            var policy = new LlmAssistTriggerPolicy();
            var allowed = CreateContext(DefaultScraperSemantic.UserRefresh, "Movie", CreateRefreshContext("?metadataRefreshMode=RefreshMetadata&replaceAllMetadata=false"));
            var rejected = CreateContext(DefaultScraperSemantic.AutomaticRefresh, "Movie", null);

            Assert.IsTrue(policy.ShouldEvaluateDeterministicMismatch(allowed));
            Assert.IsFalse(policy.ShouldEvaluateDeterministicMismatch(rejected));
        }

        [TestMethod]
        public void Evaluate_RejectsUserRefreshWithoutHttpContext()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Series", null);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("ImplicitRefreshRejected", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsAutomatedScheduledQueueRefresh()
        {
            var decision = Evaluate(DefaultScraperSemantic.AutomaticRefresh, "Series", null);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("AutomaticRefreshRejected", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsAutomaticRefreshEvenWhenMismatchWouldExist()
        {
            var decision = Evaluate(DefaultScraperSemantic.AutomaticRefresh, "Movie", null);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("AutomaticRefreshRejected", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsOverwriteRefresh()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Movie", CreateRefreshContext("?metadataRefreshMode=FullRefresh&replaceAllMetadata=true"));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("OverwriteRefreshRejected", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_AllowsOverwriteRefreshForEpisodeGroupMappingAssist()
        {
            var context = CreateContext(DefaultScraperSemantic.UserRefresh, "Series", CreateRefreshContext("?metadataRefreshMode=FullRefresh&replaceAllMetadata=true"));
            context.AllowOverwriteRefresh = true;

            var decision = new LlmAssistTriggerPolicy().Evaluate(context);

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual("ExplicitOverwriteRefresh", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_AllowsBridgedOverwriteRefreshForEpisodeGroupMappingAssist()
        {
            var context = CreateContext(DefaultScraperSemantic.UserRefresh, "Series", CreateRefreshContext(string.Empty));
            context.AllowOverwriteRefresh = true;
            context.HasBridgedExplicitOverwriteMetadataRefreshIntent = true;

            var decision = new LlmAssistTriggerPolicy().Evaluate(context);

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual("ExplicitOverwriteRefresh", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsBridgedOverwriteRefreshWithoutExplicitAllow()
        {
            var context = CreateContext(DefaultScraperSemantic.UserRefresh, "Series", CreateRefreshContext(string.Empty));
            context.HasBridgedExplicitOverwriteMetadataRefreshIntent = true;

            var decision = new LlmAssistTriggerPolicy().Evaluate(context);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("ImplicitRefreshRejected", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsImplicitScheduledFallbackWithoutHttpQuery()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Episode", CreateRefreshContext(string.Empty));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("ImplicitRefreshRejected", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsManualMatchWithoutManualApplyRoute()
        {
            var decision = Evaluate(DefaultScraperSemantic.ManualMatch, "Movie", CreateRefreshContext("?metadataRefreshMode=RefreshMetadata&replaceAllMetadata=false"));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("ImplicitRefreshRejected", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsSearchMissingWithoutExplicitFalseReplaceAllMetadata()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Movie", CreateRefreshContext("?metadataRefreshMode=FullRefresh"));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("ImplicitRefreshRejected", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsFullRefreshThatIsNotSearchMissingOrOverwrite()
        {
            var decision = Evaluate(DefaultScraperSemantic.UserRefresh, "Movie", CreateRefreshContext("?metadataRefreshMode=FullRefresh&replaceAllMetadata=unexpected"));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("ImplicitRefreshRejected", decision.Reason);
        }

        [DataTestMethod]
        [DataRow("Person")]
        [DataRow("BoxSet")]
        public void Evaluate_RejectsUnsupportedMediaTypes(string mediaType)
        {
            var decision = Evaluate(DefaultScraperSemantic.ManualMatch, mediaType, CreateManualApplyContext());

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("UnsupportedMediaType", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsImageProvider()
        {
            var decision = new LlmAssistTriggerPolicy().Evaluate(CreateContext(DefaultScraperSemantic.ManualSearch, "Movie", CreateManualApplyContext(), isImageProvider: true));

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("ImageProviderRejected", decision.Reason);
        }

        [TestMethod]
        public void Evaluate_RejectsMissingConfiguration()
        {
            var context = CreateContext(DefaultScraperSemantic.ManualMatch, "Movie", CreateManualApplyContext());
            context.Configuration!.LlmApiKey = string.Empty;

            var decision = new LlmAssistTriggerPolicy().Evaluate(context);

            Assert.IsFalse(decision.ShouldTrigger);
            Assert.AreEqual("LlmConfigurationMissing", decision.Reason);
        }

        [DataTestMethod]
        [DataRow("TextCompletionDisabled")]
        [DataRow("ImplicitRefreshRejected")]
        [DataRow("AutomaticRefreshRejected")]
        [DataRow("LlmConfigurationMissing")]
        public void Observability_ShouldLogRejectedReasonCodes(string reasonCode)
        {
            var loggerStub = new Mock<ILogger<LlmMetadataAssistService>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            LlmObservabilityLog.LogLlmAssistRejected(loggerStub.Object, reasonCode, "Movie", DefaultScraperSemantic.UserRefresh, false);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ReasonCode"] = reasonCode,
                    ["Accepted"] = false,
                    ["MediaType"] = "Movie",
                    ["Semantic"] = DefaultScraperSemantic.UserRefresh.ToString(),
                    ["IsImageProvider"] = false,
                },
                originalFormatContains: "[MetaShark] LLM 触发已评估. reason={ReasonCode} accepted={Accepted} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}",
                messageContains: ["LLM 触发已评估"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ReasonCode"] = reasonCode,
                    ["MediaType"] = "Movie",
                    ["Semantic"] = DefaultScraperSemantic.UserRefresh.ToString(),
                    ["IsImageProvider"] = false,
                },
                originalFormatContains: "[MetaShark] LLM 触发已拒绝. reason={ReasonCode} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}",
                messageContains: ["LLM 触发已拒绝"]);
        }

        [TestMethod]
        public void Evaluate_WhenLoggerProvided_ShouldLogAcceptedDecision()
        {
            var loggerStub = new Mock<ILogger<LlmAssistTriggerPolicy>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var policy = new LlmAssistTriggerPolicy(loggerStub.Object);

            var decision = policy.Evaluate(CreateContext(DefaultScraperSemantic.ManualMatch, "Movie", CreateManualApplyContext()));

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ReasonCode"] = "ManualMatch",
                    ["Accepted"] = true,
                    ["MediaType"] = "Movie",
                    ["Semantic"] = DefaultScraperSemantic.ManualMatch.ToString(),
                    ["IsImageProvider"] = false,
                },
                originalFormatContains: "[MetaShark] LLM 触发已评估. reason={ReasonCode} accepted={Accepted} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}",
                messageContains: ["LLM 触发已评估"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ReasonCode"] = "ManualMatch",
                    ["MediaType"] = "Movie",
                },
                originalFormatContains: "[MetaShark] LLM 触发已接受. reason={ReasonCode} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}",
                messageContains: ["LLM 触发已接受"]);
        }

        [TestMethod]
        public async Task AssistAsync_PassesCancellationTokenAndReturnsFailureWithoutThrowingWhenApiFails()
        {
            var api = new RecordingLlmApi(LlmApiResult.Succeeded("{\"title\":\"Inception\",\"confidence\":0.9}"), throwOnCall: true);
            var service = new LlmMetadataAssistService(api);
            using var cancellationTokenSource = new CancellationTokenSource();
            var request = new LlmScrapingAssistRequest
            {
                Configuration = CreateConfiguration(),
                LookupInfo = new MovieInfo { Name = "Inception", Path = "/mnt/media/Movies/Inception.mkv", Year = 2010 },
                MediaType = "Movie",
                Semantic = DefaultScraperSemantic.UserRefresh,
                HttpContext = CreateRefreshContext("?metadataRefreshMode=RefreshMetadata&replaceAllMetadata=false"),
                LibraryRoots = new[] { "/mnt/media" },
            };

            var result = await service.AssistAsync(request, cancellationTokenSource.Token).ConfigureAwait(false);

            Assert.AreEqual(LlmScrapingAssistStatus.Failed, result.Status);
            Assert.AreEqual(cancellationTokenSource.Token, api.CapturedCancellationToken);
            Assert.IsNotNull(result.SanitizedContext);
            Assert.IsNull(result.Suggestion);
        }

        [TestMethod]
        public async Task AssistAsync_WhenSuccessful_SendsParsedSanitizedContextInPrompt()
        {
            var api = new RecordingLlmApi(LlmApiResult.Succeeded("{\"mediaType\":\"Episode\",\"title\":\"启程\",\"confidence\":0.9}"));
            var service = new LlmMetadataAssistService(api);
            var request = new LlmScrapingAssistRequest
            {
                Configuration = CreateConfiguration(),
                LookupInfo = new EpisodeInfo
                {
                    Name = "第 1 集",
                    Path = "/mnt/media/Shows/三体/Season 01/三体.S01E01.2023.mkv",
                    MetadataLanguage = "zh-CN",
                    ParentIndexNumber = 1,
                    IndexNumber = 1,
                },
                MediaType = "Episode",
                Semantic = DefaultScraperSemantic.UserRefresh,
                HttpContext = CreateRefreshContext("?metadataRefreshMode=FullRefresh&replaceAllMetadata=false"),
                LibraryRoots = new[] { "/mnt/media" },
            };

            var result = await service.AssistAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmScrapingAssistStatus.Succeeded, result.Status, result.Diagnostic);
            Assert.AreEqual("启程", result.Suggestion!.Title);
            Assert.IsFalse(string.IsNullOrWhiteSpace(api.CapturedPrompt));
            using var document = JsonDocument.Parse(api.CapturedPrompt!);
            var root = document.RootElement;
            var context = root.TryGetProperty("Context", out var contextElement) ? contextElement : root;
            Assert.AreEqual("Episode", context.GetProperty("MediaType").GetString());
            Assert.AreEqual("三体", context.GetProperty("ParsedName").GetString());
            Assert.AreEqual(2023, context.GetProperty("ParsedYear").GetInt32());
            Assert.AreEqual(1, context.GetProperty("ParsedSeasonNumber").GetInt32());
            Assert.AreEqual(1, context.GetProperty("ParsedEpisodeNumber").GetInt32());
            Assert.AreEqual(result.SanitizedContext!.ParsedName, context.GetProperty("ParsedName").GetString());
        }

        private static LlmAssistTriggerDecision Evaluate(DefaultScraperSemantic semantic, string mediaType, HttpContext? httpContext)
        {
            return new LlmAssistTriggerPolicy().Evaluate(CreateContext(semantic, mediaType, httpContext));
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

        private sealed class RecordingLlmApi : ILlmApi
        {
            private readonly bool throwOnCall;

            private readonly LlmApiResult result;

            public RecordingLlmApi(LlmApiResult result, bool throwOnCall = false)
            {
                this.result = result;
                this.throwOnCall = throwOnCall;
            }

            public CancellationToken CapturedCancellationToken { get; private set; }

            public string? CapturedPrompt { get; private set; }

            public Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
            {
                this.CapturedPrompt = prompt;
                this.CapturedCancellationToken = cancellationToken;
                if (this.throwOnCall)
                {
                    throw new InvalidOperationException("boom");
                }

                return Task.FromResult(this.result);
            }
        }
    }
}
