using System.Text.Json;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
using Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class LlmEpisodeGroupMappingAssistTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenAssistSwitchOff_ShouldNotCallLlmOrWrite()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var configuration = CreateConfiguration(enableMappingAssist: false);
            var service = CreateService(llmApi);

            var result = await service.SuggestAndWriteAsync(CreateRequest(configuration), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.NotTriggered, result.Status);
            Assert.AreEqual(0, llmApi.Prompts.Count);
            Assert.AreEqual(string.Empty, configuration.TmdbEpisodeGroupMap);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenSeriesTmdbIdMissing_ShouldNotCallLlmOrWrite()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var configuration = CreateConfiguration();
            var service = CreateService(llmApi);
            var request = CreateRequest(configuration);
            request.SeriesTmdbId = null;

            var result = await service.SuggestAndWriteAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.NotTriggered, result.Status);
            Assert.AreEqual("SeriesTmdbIdMissing", result.Reason);
            Assert.AreEqual(0, llmApi.Prompts.Count);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenCandidateGroupsMissing_ShouldNotCallLlmOrWrite()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var configuration = CreateConfiguration();
            var service = CreateService(llmApi);
            var request = CreateRequest(configuration);
            request.CandidateGroups = Array.Empty<LlmEpisodeGroupCandidate>();

            var result = await service.SuggestAndWriteAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.NotTriggered, result.Status);
            Assert.AreEqual("CandidateGroupsMissing", result.Reason);
            Assert.AreEqual(0, llmApi.Prompts.Count);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenCandidateAccepted_ShouldWriteCanonicalMappingWithoutSensitivePromptFields()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var configuration = CreateConfiguration(existingMapping: "70000=group-b");
            var tmdbApi = CreateTmdbApi();
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var service = CreateService(llmApi, tmdbApi);
            var request = CreateRequest(configuration);

            var result = await service.SuggestAndWriteAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.Updated, result.Status);
            Assert.AreEqual("65942=candidate-group\n70000=group-b", configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual(configuration.TmdbEpisodeGroupMap, result.MappingText);
            Assert.AreEqual(1, llmApi.Prompts.Count);
            AssertPromptIsSafeAndCandidateOnly(llmApi.Prompts[0]);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenSelectedGroupOutsideCandidates_ShouldRejectWithoutWrite()
        {
            var llmApi = new RecordingLlmApi("invented-group", 0.95);
            var configuration = CreateConfiguration();
            var service = CreateService(llmApi);

            var result = await service.SuggestAndWriteAsync(CreateRequest(configuration), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.Rejected, result.Status);
            Assert.AreEqual("SelectedGroupNotInCandidates", result.Reason);
            Assert.AreEqual(string.Empty, configuration.TmdbEpisodeGroupMap);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenTmdbValidationReturnsNull_ShouldRejectWithoutWrite()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var configuration = CreateConfiguration();
            var tmdbApi = CreateTmdbApi();
            ExplicitEpisodeGroupMappingTestHelper.SeedMissingEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var service = CreateService(llmApi, tmdbApi);

            var result = await service.SuggestAndWriteAsync(CreateRequest(configuration), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.Rejected, result.Status);
            Assert.AreEqual("SelectedGroupValidationFailed", result.Reason);
            Assert.AreEqual(string.Empty, configuration.TmdbEpisodeGroupMap);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenConfidenceBelowThreshold_ShouldRejectWithoutWrite()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.79);
            var configuration = CreateConfiguration();
            var service = CreateService(llmApi);

            var result = await service.SuggestAndWriteAsync(CreateRequest(configuration), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.Rejected, result.Status);
            Assert.AreEqual("ConfidenceBelowThreshold", result.Reason);
            Assert.AreEqual(string.Empty, configuration.TmdbEpisodeGroupMap);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenSameMappingExists_ShouldNoOpWithoutDuplicateLines()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var configuration = CreateConfiguration(existingMapping: "65942=candidate-group");
            var tmdbApi = CreateTmdbApi();
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var persistenceService = new RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult.NoChange("65942=candidate-group"));
            var service = CreateService(llmApi, tmdbApi, persistenceService);

            var result = await service.SuggestAndWriteAsync(CreateRequest(configuration), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.NoChange, result.Status);
            Assert.AreEqual("65942=candidate-group", result.PreviousMapping);
            Assert.AreEqual("65942=candidate-group", configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual(1, configuration.TmdbEpisodeGroupMap.Split('\n').Length);
            Assert.AreEqual(0, persistenceService.Calls.Count);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenDifferentMappingExistsAndManual_ShouldUpdateCanonicalMapping()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var configuration = CreateConfiguration(existingMapping: "70000=group-b\n65942=old-group");
            var tmdbApi = CreateTmdbApi();
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var persistenceService = new RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult.SavedResult("65942=old-group\n70000=group-b", "65942=candidate-group\n70000=group-b"));
            var service = CreateService(llmApi, tmdbApi, persistenceService);

            var result = await service.SuggestAndWriteAsync(CreateRequest(configuration), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.Updated, result.Status);
            Assert.AreEqual("65942=old-group\n70000=group-b", result.PreviousMapping);
            Assert.AreEqual("65942=candidate-group\n70000=group-b", configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual(1, persistenceService.Calls.Count);
            Assert.AreEqual("65942=old-group\n70000=group-b", persistenceService.Calls[0].ExpectedOldMapping);
            Assert.AreEqual("65942=candidate-group\n70000=group-b", persistenceService.Calls[0].NewMapping);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenPersistenceReturnsConflict_ShouldReturnNoChangeAndKeepCurrentMapping()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var configuration = CreateConfiguration(existingMapping: "70000=group-b\n65942=old-group");
            var tmdbApi = CreateTmdbApi();
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var persistenceService = new RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult.Conflict("65942=old-group\n70000=group-b", "65942=other-group\n70000=group-b"));
            var service = CreateService(llmApi, tmdbApi, persistenceService);

            var result = await service.SuggestAndWriteAsync(CreateRequest(configuration), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.NoChange, result.Status);
            Assert.AreEqual("MappingChangedBeforeSave", result.Reason);
            Assert.AreEqual("65942=old-group\n70000=group-b", result.PreviousMapping);
            Assert.AreEqual("65942=other-group\n70000=group-b", result.MappingText);
            Assert.AreEqual("65942=other-group\n70000=group-b", configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual(1, persistenceService.Calls.Count);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenPersistenceSaveFails_ShouldReturnFailedAndKeepCurrentMapping()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var configuration = CreateConfiguration(existingMapping: "70000=group-b\n65942=old-group");
            var tmdbApi = CreateTmdbApi();
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var persistenceService = new RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult.Failed("SaveConfigurationFailed", "65942=old-group\n70000=group-b", "65942=old-group\n70000=group-b", null));
            var service = CreateService(llmApi, tmdbApi, persistenceService);

            var result = await service.SuggestAndWriteAsync(CreateRequest(configuration), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.Failed, result.Status);
            Assert.AreEqual("SaveConfigurationFailed", result.Reason);
            Assert.AreEqual("65942=old-group\n70000=group-b", result.PreviousMapping);
            Assert.AreEqual("65942=old-group\n70000=group-b", configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual(1, persistenceService.Calls.Count);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenMultipleCandidatesAndHighConfidence_ShouldPersistSelectedCandidate()
        {
            var llmApi = new RecordingLlmApi("candidate-group-2", 0.95);
            var configuration = CreateConfiguration(existingMapping: "70000=group-b");
            var tmdbApi = CreateTmdbApi();
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group-2", "zh-CN");
            var persistenceService = new RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult.SavedResult("70000=group-b", "65942=candidate-group-2\n70000=group-b"));
            var service = CreateService(llmApi, tmdbApi, persistenceService);
            var request = CreateRequest(configuration);
            request.CandidateGroups = new[]
            {
                new LlmEpisodeGroupCandidate
                {
                    GroupId = "candidate-group-1",
                    Name = "候选剧集组一",
                    Type = "storyArc",
                    GroupCount = 1,
                    EpisodeCount = 12,
                },
                new LlmEpisodeGroupCandidate
                {
                    GroupId = "candidate-group-2",
                    Name = "候选剧集组二",
                    Type = "absolute",
                    GroupCount = 2,
                    EpisodeCount = 12,
                },
            };

            var result = await service.SuggestAndWriteAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.Updated, result.Status);
            Assert.AreEqual("candidate-group-2", result.SelectedGroupId);
            Assert.AreEqual("65942=candidate-group-2\n70000=group-b", configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual(1, persistenceService.Calls.Count);
            Assert.AreEqual("65942=candidate-group-2\n70000=group-b", persistenceService.Calls[0].NewMapping);
        }

        [TestMethod]
        public async Task SuggestAndWriteAsync_WhenDifferentMappingExistsAndNonManual_ShouldNotOverwrite()
        {
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var configuration = CreateConfiguration(existingMapping: "65942=old-group");
            var service = CreateService(llmApi);
            var request = CreateRequest(configuration);
            request.IsManualTrigger = false;

            var result = await service.SuggestAndWriteAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.NotTriggered, result.Status);
            Assert.AreEqual("NonManualTriggerRejected", result.Reason);
            Assert.AreEqual("65942=old-group", configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual(0, llmApi.Prompts.Count);
        }

        private LlmEpisodeGroupMappingAssistService CreateService(RecordingLlmApi llmApi)
        {
            return this.CreateService(llmApi, CreateTmdbApi(), new RecordingPersistenceService());
        }

        private LlmEpisodeGroupMappingAssistService CreateService(RecordingLlmApi llmApi, TmdbApi tmdbApi)
        {
            return this.CreateService(llmApi, tmdbApi, new RecordingPersistenceService());
        }

        private LlmEpisodeGroupMappingAssistService CreateService(RecordingLlmApi llmApi, TmdbApi tmdbApi, ITmdbEpisodeGroupMapPersistenceService persistenceService)
        {
            return new LlmEpisodeGroupMappingAssistService(llmApi, tmdbApi, EpisodeGroupMapParser.Shared, persistenceService);
        }

        private TmdbApi CreateTmdbApi()
        {
            return new TmdbApi(this.loggerFactory);
        }

        private static PluginConfiguration CreateConfiguration(bool enableMappingAssist = true, string existingMapping = "")
        {
            return new PluginConfiguration
            {
                EnableLlmAssist = true,
                EnableLlmEpisodeGroupMappingAssist = enableMappingAssist,
                LlmEpisodeGroupMappingMinConfidence = 0.80,
                TmdbEpisodeGroupMap = existingMapping,
            };
        }

        private static LlmEpisodeGroupMappingAssistRequest CreateRequest(PluginConfiguration configuration)
        {
            return new LlmEpisodeGroupMappingAssistRequest
            {
                Configuration = configuration,
                SeriesTmdbId = 65942,
                SeriesTitle = "测试剧集",
                SafeRelativePathSamples = new[]
                {
                    "Anime/Test Series/S01E01.mkv",
                    "/opt/jellyfin/secret/S01E02.mkv",
                    "\\\\NAS\\share\\secret.mkv",
                },
                EpisodeDistribution = new[]
                {
                    new LlmEpisodeDistributionItem { SeasonNumber = 1, EpisodeCount = 12 },
                },
                CandidateGroups = new[]
                {
                    new LlmEpisodeGroupCandidate
                    {
                        GroupId = "candidate-group",
                        Name = "候选剧集组",
                        Type = "storyArc",
                        GroupCount = 2,
                        EpisodeCount = 12,
                    },
                },
                MetadataLanguage = "zh-CN",
                IsManualTrigger = true,
            };
        }

        private static void AssertPromptIsSafeAndCandidateOnly(string prompt)
        {
            Assert.IsFalse(prompt.Contains("/opt", StringComparison.OrdinalIgnoreCase), prompt);
            Assert.IsFalse(prompt.Contains("\\\\NAS", StringComparison.OrdinalIgnoreCase), prompt);
            Assert.IsFalse(prompt.Contains("ProviderIds", StringComparison.OrdinalIgnoreCase), prompt);
            Assert.IsFalse(prompt.Contains("old-group", StringComparison.OrdinalIgnoreCase), prompt);
            Assert.IsTrue(prompt.Contains("Anime/Test Series/S01E01.mkv", StringComparison.Ordinal), prompt);
            Assert.IsTrue(prompt.Contains("candidate-group", StringComparison.Ordinal), prompt);

            using var document = JsonDocument.Parse(prompt);
            var root = document.RootElement;
            Assert.IsTrue(root.TryGetProperty("candidateGroups", out var candidateGroups));
            Assert.AreEqual(1, candidateGroups.GetArrayLength());
            Assert.AreEqual("candidate-group", candidateGroups[0].GetProperty("groupId").GetString());
        }

        private sealed class RecordingLlmApi : ILlmApi
        {
            private readonly string selectedGroupId;
            private readonly double confidence;

            public RecordingLlmApi(string selectedGroupId, double confidence)
            {
                this.selectedGroupId = selectedGroupId;
                this.confidence = confidence;
            }

            public List<string> Prompts { get; } = new();

            public Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
            {
                this.Prompts.Add(prompt);
                var content = JsonSerializer.Serialize(new
                {
                    selectedGroupId = this.selectedGroupId,
                    confidence = this.confidence,
                    reason = "matches season distribution",
                });
                return Task.FromResult(LlmApiResult.Succeeded(content));
            }
        }

        private sealed class RecordingPersistenceService : ITmdbEpisodeGroupMapPersistenceService
        {
            private readonly TmdbEpisodeGroupMapPersistenceResult? result;

            public RecordingPersistenceService()
            {
            }

            public RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult result)
            {
                this.result = result;
            }

            public List<PersistenceCall> Calls { get; } = new();

            public Task<TmdbEpisodeGroupMapPersistenceResult> TrySaveAsync(string? expectedOldMapping, string? newMapping, CancellationToken cancellationToken)
            {
                this.Calls.Add(new PersistenceCall(expectedOldMapping ?? string.Empty, newMapping ?? string.Empty));
                return Task.FromResult(this.result ?? TmdbEpisodeGroupMapPersistenceResult.SavedResult(expectedOldMapping ?? string.Empty, newMapping ?? string.Empty));
            }
        }

        private sealed record PersistenceCall(string ExpectedOldMapping, string NewMapping);
    }
}
