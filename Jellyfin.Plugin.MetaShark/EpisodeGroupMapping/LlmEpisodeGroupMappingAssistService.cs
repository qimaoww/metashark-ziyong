// <copyright file="LlmEpisodeGroupMappingAssistService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Providers.Llm;

    public sealed class LlmEpisodeGroupMappingAssistService : ILlmEpisodeGroupMappingAssistService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly ILlmApi llmApi;
        private readonly TmdbApi tmdbApi;
        private readonly EpisodeGroupMapParser parser;
        private readonly ITmdbEpisodeGroupMapPersistenceService persistenceService;
        private readonly ILlmRequestLimiter requestLimiter;

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Compatibility constructor owns a process-local fallback limiter for test-only direct construction.")]
        public LlmEpisodeGroupMappingAssistService(ILlmApi llmApi, TmdbApi tmdbApi)
            : this(llmApi, tmdbApi, EpisodeGroupMapParser.Shared, new TmdbEpisodeGroupMapPersistenceService(EpisodeGroupMapParser.Shared), new LlmRequestLimiter())
        {
        }

        public LlmEpisodeGroupMappingAssistService(ILlmApi llmApi, TmdbApi tmdbApi, EpisodeGroupMapParser parser, ILlmRequestLimiter? requestLimiter = null)
            : this(llmApi, tmdbApi, parser, new TmdbEpisodeGroupMapPersistenceService(parser), requestLimiter)
        {
        }

        public LlmEpisodeGroupMappingAssistService(ILlmApi llmApi, TmdbApi tmdbApi, EpisodeGroupMapParser parser, ITmdbEpisodeGroupMapPersistenceService persistenceService, ILlmRequestLimiter? requestLimiter = null)
        {
            this.llmApi = llmApi ?? throw new ArgumentNullException(nameof(llmApi));
            this.tmdbApi = tmdbApi ?? throw new ArgumentNullException(nameof(tmdbApi));
            this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
            this.persistenceService = persistenceService ?? throw new ArgumentNullException(nameof(persistenceService));
            this.requestLimiter = requestLimiter ?? new LlmRequestLimiter();
        }

        public async Task<LlmEpisodeGroupMappingAssistResult> SuggestAndWriteAsync(LlmEpisodeGroupMappingAssistRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var configuration = request.Configuration;
            var currentMapping = configuration?.TmdbEpisodeGroupMap ?? string.Empty;
            if (configuration == null || !configuration.EnableLlmAssist || !configuration.EnableLlmEpisodeGroupMappingAssist)
            {
                return LlmEpisodeGroupMappingAssistResult.NotTriggered("LlmEpisodeGroupMappingAssistDisabled", currentMapping);
            }

            if (!request.IsManualTrigger)
            {
                return LlmEpisodeGroupMappingAssistResult.NotTriggered("NonManualTriggerRejected", currentMapping);
            }

            if (!request.SeriesTmdbId.HasValue || request.SeriesTmdbId.Value <= 0)
            {
                return LlmEpisodeGroupMappingAssistResult.NotTriggered("SeriesTmdbIdMissing", currentMapping);
            }

            var candidates = NormalizeCandidates(request.CandidateGroups, configuration.LlmEpisodeGroupMappingMaxCandidateGroups);
            if (candidates.Count == 0)
            {
                return LlmEpisodeGroupMappingAssistResult.NotTriggered("CandidateGroupsMissing", currentMapping);
            }

            using var lease = await this.requestLimiter.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
            if (lease == null)
            {
                return LlmEpisodeGroupMappingAssistResult.NotTriggered("LlmRequestLimiterBusy", currentMapping);
            }

            var apiResult = await this.llmApi.CompleteAsync(BuildPrompt(request, candidates), LlmResponseSchemaKind.EpisodeGroupMapping, cancellationToken).ConfigureAwait(false);
            if (!apiResult.Success || string.IsNullOrWhiteSpace(apiResult.ContentJson))
            {
                return LlmEpisodeGroupMappingAssistResult.Failed(apiResult.Diagnostic, currentMapping);
            }

            var suggestion = ParseSuggestion(apiResult.ContentJson);
            if (suggestion == null)
            {
                return LlmEpisodeGroupMappingAssistResult.Failed("LLM episode group suggestion schema invalid.", currentMapping);
            }

            var selectedGroupId = suggestion.SelectedGroupId?.Trim();
            if (string.IsNullOrWhiteSpace(selectedGroupId))
            {
                return LlmEpisodeGroupMappingAssistResult.Rejected("SelectedGroupIdMissing", currentMapping);
            }

            if (double.IsNaN(suggestion.Confidence) || suggestion.Confidence < 0 || suggestion.Confidence > 1)
            {
                return LlmEpisodeGroupMappingAssistResult.Rejected("ConfidenceOutOfRange", currentMapping, selectedGroupId);
            }

            if (suggestion.Confidence < configuration.LlmEpisodeGroupMappingMinConfidence)
            {
                return LlmEpisodeGroupMappingAssistResult.Rejected("ConfidenceBelowThreshold", currentMapping, selectedGroupId);
            }

            if (!candidates.Any(candidate => string.Equals(candidate.GroupId, selectedGroupId, StringComparison.Ordinal)))
            {
                return LlmEpisodeGroupMappingAssistResult.Rejected("SelectedGroupNotInCandidates", currentMapping, selectedGroupId);
            }

            var validatedGroup = await this.tmdbApi.GetEpisodeGroupByIdAsync(selectedGroupId, request.MetadataLanguage, cancellationToken).ConfigureAwait(false);
            if (validatedGroup == null)
            {
                return LlmEpisodeGroupMappingAssistResult.Rejected("SelectedGroupValidationFailed", currentMapping, selectedGroupId);
            }

            var seriesIdText = request.SeriesTmdbId.Value.ToString(CultureInfo.InvariantCulture);
            var snapshot = this.parser.ParseSnapshot(currentMapping);
            if (snapshot.TryGetGroupId(seriesIdText, out var existingGroupId))
            {
                if (string.Equals(existingGroupId, selectedGroupId, StringComparison.Ordinal))
                {
                    return LlmEpisodeGroupMappingAssistResult.NoChange("ExistingMappingAlreadyMatches", snapshot.CanonicalText, selectedGroupId);
                }
            }

            var newMapping = this.UpsertCanonicalMapping(snapshot.CanonicalText, seriesIdText, selectedGroupId);
            var persistenceResult = await this.persistenceService.TrySaveAsync(snapshot.CanonicalText, newMapping, cancellationToken).ConfigureAwait(false);
            configuration.TmdbEpisodeGroupMap = persistenceResult.CurrentMapping;
            return MapPersistenceResult(persistenceResult, selectedGroupId);
        }

        private static LlmEpisodeGroupMappingAssistResult MapPersistenceResult(TmdbEpisodeGroupMapPersistenceResult persistenceResult, string selectedGroupId)
        {
            ArgumentNullException.ThrowIfNull(persistenceResult);

            return persistenceResult.Status switch
            {
                TmdbEpisodeGroupMapPersistenceStatus.Saved => LlmEpisodeGroupMappingAssistResult.Updated(persistenceResult.PreviousMapping, persistenceResult.CurrentMapping, selectedGroupId),
                TmdbEpisodeGroupMapPersistenceStatus.NoChange => LlmEpisodeGroupMappingAssistResult.NoChange(persistenceResult.Reason, persistenceResult.CurrentMapping, selectedGroupId, persistenceResult.PreviousMapping),
                TmdbEpisodeGroupMapPersistenceStatus.Conflict => LlmEpisodeGroupMappingAssistResult.NoChange(persistenceResult.Reason, persistenceResult.CurrentMapping, selectedGroupId, persistenceResult.PreviousMapping),
                TmdbEpisodeGroupMapPersistenceStatus.Failed => LlmEpisodeGroupMappingAssistResult.Failed(persistenceResult.Reason, persistenceResult.CurrentMapping, selectedGroupId, persistenceResult.PreviousMapping),
                _ => LlmEpisodeGroupMappingAssistResult.Failed("UnknownPersistenceStatus", persistenceResult.CurrentMapping, selectedGroupId, persistenceResult.PreviousMapping),
            };
        }

        private static List<LlmEpisodeGroupCandidate> NormalizeCandidates(IEnumerable<LlmEpisodeGroupCandidate?> candidates, int maxCandidateGroups)
        {
            var result = new List<LlmEpisodeGroupCandidate>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates ?? Array.Empty<LlmEpisodeGroupCandidate?>())
            {
                var groupId = candidate?.GroupId?.Trim();
                if (string.IsNullOrWhiteSpace(groupId) || !seenIds.Add(groupId))
                {
                    continue;
                }

                result.Add(new LlmEpisodeGroupCandidate
                {
                    GroupId = groupId,
                    Name = NormalizeText(candidate!.Name),
                    Type = NormalizeText(candidate.Type),
                    GroupCount = candidate.GroupCount,
                    EpisodeCount = candidate.EpisodeCount,
                });

                if (result.Count >= maxCandidateGroups)
                {
                    break;
                }
            }

            return result;
        }

        private static string BuildPrompt(LlmEpisodeGroupMappingAssistRequest request, IReadOnlyList<LlmEpisodeGroupCandidate> candidates)
        {
            var payload = new
            {
                task = "Select one TMDB episode group id from candidateGroups for this series. Return JSON only with selectedGroupId, confidence, reason.",
                seriesTmdbId = request.SeriesTmdbId,
                seriesTitle = NormalizeText(request.SeriesTitle),
                metadataLanguage = NormalizeText(request.MetadataLanguage),
                safeRelativePathSamples = NormalizeSamples(request.SafeRelativePathSamples),
                episodeDistribution = NormalizeDistribution(request.EpisodeDistribution),
                candidateGroups = candidates,
                constraints = new[]
                {
                    "selectedGroupId must exactly equal one candidate groupId",
                    "do not invent group ids",
                    "use only relative path/file summaries",
                    "DO: select exactly one selectedGroupId from candidateGroups only when it matches the supplied episodeDistribution.",
                    "DO: set confidence from 0.0 to 1.0 and explain the season/episode distribution evidence in reason.",
                    "DO NOT: invent group ids, use group names as ids, select by popularity, or select a group that is not present in candidateGroups.",
                    "DO NOT: output metadata, ProviderIds, extra TMDb series/movie/season/episode IDs, titles, images, people, scraper mode changes, refresh actions, or configuration text.",
                    "DO NOT: request a metadata refresh, change scraper source, or treat the selected episode group as proof that another TMDb series id is correct.",
                    "DO NOT: treat relative paths or filenames as authoritative when they conflict with candidateGroups or episodeDistribution.",
                    "If no candidate clearly matches, return selectedGroupId as an empty string with confidence 0.0 and a reason.",
                },
            };

            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        private static LlmEpisodeGroupSelection? ParseSuggestion(string contentJson)
        {
            try
            {
                using var document = JsonDocument.Parse(contentJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                foreach (var property in root.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "selectedGroupId", StringComparison.Ordinal)
                        && !string.Equals(property.Name, "confidence", StringComparison.Ordinal)
                        && !string.Equals(property.Name, "reason", StringComparison.Ordinal))
                    {
                        return null;
                    }
                }

                if (!root.TryGetProperty("selectedGroupId", out var groupIdProperty) || groupIdProperty.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                if (!root.TryGetProperty("confidence", out var confidenceProperty) || confidenceProperty.ValueKind != JsonValueKind.Number || !confidenceProperty.TryGetDouble(out var confidence))
                {
                    return null;
                }

                return new LlmEpisodeGroupSelection
                {
                    SelectedGroupId = groupIdProperty.GetString(),
                    Confidence = confidence,
                    Reason = root.TryGetProperty("reason", out var reasonProperty) && reasonProperty.ValueKind == JsonValueKind.String ? reasonProperty.GetString() : null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string[] NormalizeSamples(IEnumerable<string?> samples)
        {
            return (samples ?? Array.Empty<string?>())
                .Select(NormalizeText)
                .Where(sample => !string.IsNullOrWhiteSpace(sample) && !LooksSensitivePath(sample!))
                .Take(20)
                .Select(sample => sample!)
                .ToArray();
        }

        private static LlmEpisodeDistributionItem[] NormalizeDistribution(IEnumerable<LlmEpisodeDistributionItem?> distribution)
        {
            return (distribution ?? Array.Empty<LlmEpisodeDistributionItem?>())
                .Where(item => item != null && item.SeasonNumber >= 0 && item.EpisodeCount > 0)
                .Select(item => new LlmEpisodeDistributionItem
                {
                    SeasonNumber = item!.SeasonNumber,
                    EpisodeCount = item.EpisodeCount,
                })
                .OrderBy(item => item.SeasonNumber)
                .ToArray();
        }

        private static bool LooksSensitivePath(string value)
        {
            return value.StartsWith('/')
                || value.StartsWith('~')
                || value.Contains('\\', StringComparison.Ordinal)
                || value.Contains(":/", StringComparison.Ordinal)
                || value.Contains("/root/", StringComparison.OrdinalIgnoreCase)
                || value.Contains("/home/", StringComparison.OrdinalIgnoreCase)
                || value.Contains("/mnt/", StringComparison.OrdinalIgnoreCase)
                || value.Contains("/opt/", StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private string UpsertCanonicalMapping(string currentCanonicalMapping, string seriesId, string groupId)
        {
            var lines = string.IsNullOrWhiteSpace(currentCanonicalMapping)
                ? new List<string>()
                : currentCanonicalMapping.Split('\n').ToList();
            var updated = false;
            for (var index = 0; index < lines.Count; index++)
            {
                var separatorIndex = lines[index].IndexOf('=', StringComparison.Ordinal);
                if (separatorIndex < 0)
                {
                    continue;
                }

                var existingSeriesId = lines[index][..separatorIndex].Trim();
                if (!string.Equals(existingSeriesId, seriesId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lines[index] = $"{seriesId}={groupId}";
                updated = true;
                break;
            }

            if (!updated)
            {
                lines.Add($"{seriesId}={groupId}");
            }

            return this.parser.GetCanonicalText(string.Join("\n", lines));
        }

        private sealed class LlmEpisodeGroupSelection
        {
            public string? SelectedGroupId { get; set; }

            public double Confidence { get; set; }

            public string? Reason { get; set; }
        }
    }
}
