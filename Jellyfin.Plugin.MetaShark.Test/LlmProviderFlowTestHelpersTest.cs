using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class LlmProviderFlowTestHelpersTest
    {
        [TestMethod]
        public async Task RecordingLlmMetadataAssistService_RecordsRequestAndReturnsQueuedResult()
        {
            var service = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            var expectedContext = new LlmPromptContext
            {
                MediaType = "Movie",
                RelativePath = "Movies/Inception/Inception.mkv",
            };
            var expectedSuggestion = new LlmScrapingSuggestion
            {
                MediaType = "Movie",
                Title = "盗梦空间",
                Year = 2010,
                Confidence = 0.94,
            };
            var expectedHints = new LlmSearchHints
            {
                Title = "Inception",
                Year = 2010,
            };
            service.EnqueueResult(LlmScrapingAssistResult.Succeeded(expectedContext, expectedSuggestion, expectedHints));

            var request = new LlmScrapingAssistRequest
            {
                Configuration = new PluginConfiguration(),
                LookupInfo = new MovieInfo
                {
                    Name = "Inception",
                    Path = "Movies/Inception/Inception.mkv",
                },
                MediaType = "Movie",
                HttpContext = LlmProviderFlowTestHelpers.CreateManualMatchHttpContext("42"),
                LibraryRoots = new[] { "library" },
            };

            var result = await service.AssistAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, service.Requests.Count);
            Assert.AreSame(request, service.Requests[0]);
            Assert.AreEqual(LlmScrapingAssistStatus.Succeeded, result.Status);
            Assert.AreEqual("Movies/Inception/Inception.mkv", result.SanitizedContext!.RelativePath);
            Assert.AreEqual("盗梦空间", result.Suggestion!.Title);
            Assert.AreEqual("Inception", result.SearchHints.Title);
        }

        [TestMethod]
        public void CreateHttpContexts_BuildExpectedManualRefreshAndAutomaticShapes()
        {
            var manualMatch = LlmProviderFlowTestHelpers.CreateManualMatchHttpContext("123");
            var explicitRefresh = LlmProviderFlowTestHelpers.CreateExplicitRefreshHttpContext("abc", metadataRefreshMode: "RefreshMetadata", replaceAllMetadata: true);
            var searchMissing = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext("xyz", metadataRefreshMode: "FullRefresh", replaceAllMetadata: false);
            var automaticRefreshAccessor = LlmProviderFlowTestHelpers.CreateAutomaticRefreshContextAccessor();

            Assert.AreEqual(HttpMethods.Post, manualMatch.Request.Method);
            Assert.AreEqual("/Items/RemoteSearch/Apply/123", manualMatch.Request.Path.Value);

            Assert.AreEqual(HttpMethods.Post, explicitRefresh.Request.Method);
            Assert.AreEqual("/Items/abc/Refresh", explicitRefresh.Request.Path.Value);
            Assert.AreEqual("?metadataRefreshMode=RefreshMetadata&replaceAllMetadata=true", explicitRefresh.Request.QueryString.Value);

            Assert.AreEqual(HttpMethods.Post, searchMissing.Request.Method);
            Assert.AreEqual("/Items/xyz/Refresh", searchMissing.Request.Path.Value);
            Assert.AreEqual("?metadataRefreshMode=FullRefresh&replaceAllMetadata=false", searchMissing.Request.QueryString.Value);

            Assert.IsNull(automaticRefreshAccessor.HttpContext);
        }

        [TestMethod]
        public void AssertNoSensitiveContent_AllowsSafeRelativeInputsAndSanitizedContext()
        {
            var request = new LlmScrapingAssistRequest
            {
                LookupInfo = new EpisodeInfo
                {
                    Name = "第 1 集",
                    Path = "Shows/Series A/Season 01/S01E01.mkv",
                },
                MediaType = "Episode",
                HttpContext = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext("1"),
                LibraryRoots = new[] { "library", "safe-root" },
            };
            var result = LlmScrapingAssistResult.Succeeded(
                new LlmPromptContext
                {
                    MediaType = "Episode",
                    RelativePath = "Shows/Series A/Season 01/S01E01.mkv",
                    FileName = "S01E01.mkv",
                },
                new LlmScrapingSuggestion
                {
                    MediaType = "Episode",
                    Title = "皇后回宫",
                    EpisodeNumber = 1,
                    Confidence = 0.91,
                },
                new LlmSearchHints
                {
                    Title = "Series A",
                });

            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(request, result, "safe prompt", "relative/context-only");
        }

        [TestMethod]
        public void AssertNoSensitiveContent_RejectsAbsolutePathLeak()
        {
            var exception = Assert.ThrowsException<AssertFailedException>(() => LlmProviderFlowTestHelpers.AssertNoSensitiveContent("prompt leaked /mnt/media/Shows/Series A/S01E01.mkv"));

            StringAssert.Contains(exception.Message, "/mnt");
        }

        [TestMethod]
        public void AssertProviderIdsUnchanged_MatchesSnapshot()
        {
            var original = new Dictionary<string, string>
            {
                ["Tmdb"] = "123",
                ["MetaShark"] = "Tmdb_123",
            };
            var snapshot = LlmProviderFlowTestHelpers.CloneProviderIds(original);
            var current = new Dictionary<string, string>
            {
                ["MetaShark"] = "Tmdb_123",
                ["Tmdb"] = "123",
            };

            LlmProviderFlowTestHelpers.AssertProviderIdsUnchanged(snapshot, current);
        }
    }
}
