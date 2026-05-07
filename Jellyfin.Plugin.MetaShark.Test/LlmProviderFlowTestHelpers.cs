using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MetaShark.Test
{
    internal static class LlmProviderFlowTestHelpers
    {
        private static readonly string[] ForbiddenPrivacyFragments =
        {
            "/mnt",
            "/root",
            "/home",
            "/opt",
            "C:\\",
            "\\\\",
            "sk-test-secret",
        };

        public static HttpContext CreateManualMatchHttpContext(string itemId = "1")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/Items/RemoteSearch/Apply/{itemId}";
            return context;
        }

        public static HttpContext CreateExplicitRefreshHttpContext(string itemId, string metadataRefreshMode = "RefreshMetadata", bool replaceAllMetadata = false)
        {
            return CreateRefreshHttpContext(itemId, metadataRefreshMode, replaceAllMetadata);
        }

        public static HttpContext CreateExplicitSearchMissingHttpContext(string itemId, string metadataRefreshMode = "FullRefresh", bool replaceAllMetadata = false)
        {
            return CreateRefreshHttpContext(itemId, metadataRefreshMode, replaceAllMetadata);
        }

        public static HttpContext? CreateAutomaticRefreshHttpContext()
        {
            return null;
        }

        public static IHttpContextAccessor CreateManualMatchContextAccessor(string itemId = "1")
        {
            return new HttpContextAccessor
            {
                HttpContext = CreateManualMatchHttpContext(itemId),
            };
        }

        public static IHttpContextAccessor CreateExplicitRefreshContextAccessor(string itemId, string metadataRefreshMode = "RefreshMetadata", bool replaceAllMetadata = false)
        {
            return new HttpContextAccessor
            {
                HttpContext = CreateExplicitRefreshHttpContext(itemId, metadataRefreshMode, replaceAllMetadata),
            };
        }

        public static IHttpContextAccessor CreateExplicitSearchMissingContextAccessor(string itemId, string metadataRefreshMode = "FullRefresh", bool replaceAllMetadata = false)
        {
            return new HttpContextAccessor
            {
                HttpContext = CreateExplicitSearchMissingHttpContext(itemId, metadataRefreshMode, replaceAllMetadata),
            };
        }

        public static IHttpContextAccessor CreateAutomaticRefreshContextAccessor()
        {
            return new HttpContextAccessor
            {
                HttpContext = CreateAutomaticRefreshHttpContext(),
            };
        }

        public static Dictionary<string, string>? CloneProviderIds(IReadOnlyDictionary<string, string>? providerIds)
        {
            return providerIds == null ? null : new Dictionary<string, string>(providerIds);
        }

        public static void AssertProviderIdsUnchanged(IReadOnlyDictionary<string, string>? expected, IReadOnlyDictionary<string, string>? actual, string label = "ProviderIds")
        {
            if (expected == null || actual == null)
            {
                Assert.AreEqual(expected == null, actual == null, $"{label} null state changed.");
                return;
            }

            CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray(), $"{label} keys changed.");
            foreach (var entry in expected)
            {
                Assert.IsTrue(actual.TryGetValue(entry.Key, out var value), $"{label} missing key {entry.Key}.");
                Assert.AreEqual(entry.Value, value, $"{label}[{entry.Key}] changed.");
            }
        }

        public static void AssertNoSensitiveContent(LlmScrapingAssistRequest request, LlmScrapingAssistResult? result = null, params string?[] additionalTexts)
        {
            var texts = new List<string?>
            {
                request.LookupInfo?.Path,
                request.HttpContext?.Request.Path.Value,
                request.HttpContext?.Request.QueryString.Value,
                request.MediaType,
            };

            texts.AddRange(request.LibraryRoots);
            texts.AddRange(additionalTexts);

            if (result?.SanitizedContext != null)
            {
                texts.Add(JsonSerializer.Serialize(result.SanitizedContext));
            }

            foreach (var text in texts)
            {
                AssertNoSensitiveContent(text);
            }
        }

        public static void AssertNoSensitiveContent(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            foreach (var forbiddenFragment in ForbiddenPrivacyFragments)
            {
                Assert.IsFalse(
                    text.Contains(forbiddenFragment, StringComparison.OrdinalIgnoreCase),
                    $"LLM request/prompt/context leaked sensitive fragment '{forbiddenFragment}': {text}");
            }
        }

        public static void AssertNoSensitiveContent(params string?[] texts)
        {
            foreach (var text in texts)
            {
                AssertNoSensitiveContent(text);
            }
        }

        private static HttpContext CreateRefreshHttpContext(string itemId, string metadataRefreshMode, bool replaceAllMetadata)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/Items/{itemId}/Refresh";
            context.Request.QueryString = new QueryString($"?metadataRefreshMode={metadataRefreshMode}&replaceAllMetadata={replaceAllMetadata.ToString().ToLowerInvariant()}");
            return context;
        }


        internal sealed class RecordingLlmExternalIdResolutionService : ILlmExternalIdResolutionService
        {
            private readonly Queue<LlmExternalIdResolutionResult> queuedResults = new Queue<LlmExternalIdResolutionResult>();

            public List<LlmExternalIdResolutionRequest> Requests { get; } = new List<LlmExternalIdResolutionRequest>();

            public void EnqueueResult(LlmExternalIdResolutionResult result)
            {
                this.queuedResults.Enqueue(result);
            }

            public Task<LlmExternalIdResolutionResult> ResolveAsync(LlmExternalIdResolutionRequest request, CancellationToken cancellationToken)
            {
                this.Requests.Add(request);
                var result = this.queuedResults.Count > 0
                    ? this.queuedResults.Dequeue()
                    : LlmExternalIdResolutionResult.NotTriggered("No queued test result.");
                return Task.FromResult(result);
            }
        }

        internal sealed class RecordingLlmMetadataAssistService : ILlmMetadataAssistService
        {
            private readonly Queue<LlmScrapingAssistResult> queuedResults = new Queue<LlmScrapingAssistResult>();

            public List<LlmScrapingAssistRequest> Requests { get; } = new List<LlmScrapingAssistRequest>();

            public void EnqueueResult(LlmScrapingAssistResult result)
            {
                this.queuedResults.Enqueue(result);
            }

            public Task<LlmScrapingAssistResult> AssistAsync(LlmScrapingAssistRequest request, CancellationToken cancellationToken)
            {
                this.Requests.Add(request);
                var result = this.queuedResults.Count > 0
                    ? this.queuedResults.Dequeue()
                    : LlmScrapingAssistResult.NotTriggered("No queued test result.");
                return Task.FromResult(result);
            }
        }
    }
}
