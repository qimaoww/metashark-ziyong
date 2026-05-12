// <copyright file="LlmExternalIdResolutionService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Objects.Find;
    using TMDbLib.Objects.TvShows;

    [SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1201:Elements should appear in the correct order", Justification = "Internal helper types stay near the logic they support for this orchestration-heavy service.")]
    [SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before non-static members", Justification = "Keep orchestration flow before helper methods.")]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Verification methods are intentionally instance methods for testable service composition.")]
    public sealed class LlmExternalIdResolutionService : ILlmExternalIdResolutionService
    {
        private const string MovieMediaType = "Movie";
        private const string SeriesMediaType = "Series";
        private const string SeasonMediaType = "Season";
        private const string EpisodeMediaType = "Episode";
        private const string TmdbProvider = "TMDb";
        private const string ImdbProvider = "IMDb";
        private const string TvdbProvider = "TVDB";
        private const string DoubanProvider = "Douban";

        private readonly ILlmApi llmApi;
        private readonly TmdbApi tmdbApi;
        private readonly DoubanApi doubanApi;
        private readonly TvdbApi tvdbApi;
        private readonly LlmAssistTriggerPolicy triggerPolicy;
        private readonly LlmTmdbIdCorrectionTriggerPolicy tmdbCorrectionTriggerPolicy;
        private readonly LlmExternalIdCandidateValidator candidateValidator;
        private readonly ILlmRequestLimiter requestLimiter;
        private readonly LlmScrapeContextBuilder scrapeContextBuilder = new LlmScrapeContextBuilder();
        private readonly LlmScrapeMismatchDetector mismatchDetector = new LlmScrapeMismatchDetector();
        private readonly ILogger<LlmExternalIdResolutionService>? logger;

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Compatibility constructor owns a process-local fallback limiter for test-only direct construction.")]
        public LlmExternalIdResolutionService(ILlmApi llmApi, TmdbApi tmdbApi, DoubanApi doubanApi, TvdbApi tvdbApi)
            : this(llmApi, tmdbApi, doubanApi, tvdbApi, new LlmAssistTriggerPolicy(), new LlmExternalIdCandidateValidator(), new LlmRequestLimiter())
        {
        }

        public LlmExternalIdResolutionService(
            ILlmApi llmApi,
            TmdbApi tmdbApi,
            DoubanApi doubanApi,
            TvdbApi tvdbApi,
            LlmAssistTriggerPolicy triggerPolicy,
            LlmExternalIdCandidateValidator candidateValidator,
            ILlmRequestLimiter? requestLimiter = null)
            : this(llmApi, tmdbApi, doubanApi, tvdbApi, triggerPolicy, new LlmTmdbIdCorrectionTriggerPolicy(), candidateValidator, requestLimiter)
        {
        }

        public LlmExternalIdResolutionService(
            ILlmApi llmApi,
            TmdbApi tmdbApi,
            DoubanApi doubanApi,
            TvdbApi tvdbApi,
            LlmAssistTriggerPolicy triggerPolicy,
            LlmTmdbIdCorrectionTriggerPolicy tmdbCorrectionTriggerPolicy,
            LlmExternalIdCandidateValidator candidateValidator,
            ILlmRequestLimiter? requestLimiter = null,
            ILogger<LlmExternalIdResolutionService>? logger = null)
        {
            this.llmApi = llmApi ?? throw new ArgumentNullException(nameof(llmApi));
            this.tmdbApi = tmdbApi ?? throw new ArgumentNullException(nameof(tmdbApi));
            this.doubanApi = doubanApi ?? throw new ArgumentNullException(nameof(doubanApi));
            this.tvdbApi = tvdbApi ?? throw new ArgumentNullException(nameof(tvdbApi));
            this.triggerPolicy = triggerPolicy ?? throw new ArgumentNullException(nameof(triggerPolicy));
            this.tmdbCorrectionTriggerPolicy = tmdbCorrectionTriggerPolicy ?? throw new ArgumentNullException(nameof(tmdbCorrectionTriggerPolicy));
            this.candidateValidator = candidateValidator ?? throw new ArgumentNullException(nameof(candidateValidator));
            this.requestLimiter = requestLimiter ?? new LlmRequestLimiter();
            this.logger = logger;
        }

        public async Task<LlmAssistTriggerDecision> EvaluateExistingProviderIdsAsync(LlmExternalIdResolutionRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.LookupInfo == null)
            {
                return LlmAssistTriggerDecision.Allowed("LookupInfoMissing");
            }

            var mediaType = NormalizeMediaType(request.MediaType ?? GetMediaTypeFromLookupInfo(request.LookupInfo));
            var existingProviderIds = EnumerateExistingProviderIds(request.LookupInfo, mediaType).ToArray();
            if (existingProviderIds.Length == 0)
            {
                return LlmAssistTriggerDecision.Allowed("NoExistingProviderIds");
            }

            var allowRelativePathContext = request.Configuration?.LlmAllowRelativePathContext ?? true;
            var localContext = this.scrapeContextBuilder.Build(request.LookupInfo, mediaType, request.LibraryRoots, allowRelativePathContext);
            var externalIdPromptContext = LlmPromptContextBuilder.BuildExternalIdPromptContext(request.LookupInfo, mediaType, request.LibraryRoots, request.RelativePathSamples, allowRelativePathContext);
            if (externalIdPromptContext.SafeRelativePathSamples.Count > 0)
            {
                localContext.ParentFolderName = string.Join(" ", externalIdPromptContext.SafeRelativePathSamples);
            }

            foreach (var existingProviderId in existingProviderIds)
            {
                var assessment = await this.AssessExistingProviderIdAsync(existingProviderId, request.LookupInfo, mediaType, localContext, cancellationToken).ConfigureAwait(false);
                if (assessment == ExistingProviderIdAssessment.Conflict)
                {
                    return LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict");
                }
            }

            if (IsTmdbCorrectionSupportedMediaType(mediaType)
                && !TryGetProviderId(request.LookupInfo.ProviderIds, MetadataProvider.Tmdb.ToString(), out _))
            {
                return LlmAssistTriggerDecision.Allowed("MissingTmdbProviderId");
            }

            return LlmAssistTriggerDecision.Rejected("ExistingProviderIdsConsistent");
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "LLM external ID resolution must fail closed and preserve ProviderIds.")]
        public async Task<LlmExternalIdResolutionResult> ResolveAsync(LlmExternalIdResolutionRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.LookupInfo == null)
            {
                LlmObservabilityLog.LogLlmAssistRejected(this.logger, "LookupInfoMissing", request.MediaType, request.Semantic, request.IsImageProvider);
                LlmObservabilityLog.LogExternalIdResolutionCompleted(this.logger, nameof(LlmExternalIdResolutionStatus.NotTriggered), "LookupInfoMissing", request.MediaType, 0, 0, 0, 0, 0, null);
                return LlmExternalIdResolutionResult.NotTriggered("LookupInfoMissing");
            }

            var mediaType = NormalizeMediaType(request.MediaType ?? GetMediaTypeFromLookupInfo(request.LookupInfo));
            var triggerDecision = this.triggerPolicy.Evaluate(new LlmAssistTriggerContext
            {
                Configuration = request.Configuration,
                Semantic = request.Semantic,
                MediaType = mediaType,
                IsImageProvider = request.IsImageProvider,
                HttpContext = request.HttpContext,
                HasBridgedExplicitSearchMissingMetadataRefreshIntent = request.HasBridgedExplicitSearchMissingMetadataRefreshIntent,
            });
            if (!triggerDecision.ShouldTrigger)
            {
                LlmObservabilityLog.LogExternalIdResolutionCompleted(this.logger, nameof(LlmExternalIdResolutionStatus.NotTriggered), triggerDecision.Reason, mediaType, 0, 0, 0, 0, 0, null);
                return LlmExternalIdResolutionResult.NotTriggered(triggerDecision.Reason);
            }

            var allowRelativePathContext = request.Configuration?.LlmAllowRelativePathContext ?? true;
            var prompt = LlmPromptContextBuilder.BuildExternalIdPromptJson(request.LookupInfo, mediaType, request.LibraryRoots, request.RelativePathSamples, allowRelativePathContext);
            LlmApiResult apiResult;
            try
            {
                using var lease = await this.requestLimiter.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
                if (lease == null)
                {
                    LlmObservabilityLog.LogLlmAssistRejected(this.logger, "LlmRequestLimiterBusy", mediaType, request.Semantic, request.IsImageProvider);
                    LlmObservabilityLog.LogExternalIdResolutionCompleted(this.logger, nameof(LlmExternalIdResolutionStatus.Skipped), "LlmRequestLimiterBusy", mediaType, 0, 0, 0, 0, 0, null);
                    return LlmExternalIdResolutionResult.Skipped("LlmRequestLimiterBusy");
                }

                apiResult = await this.llmApi.CompleteAsync(prompt, LlmResponseSchemaKind.ExternalIdCandidates, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var diagnostic = $"LLM external ID request failed: {ex.GetType().Name}";
                LlmObservabilityLog.LogExternalIdResolutionCompleted(this.logger, nameof(LlmExternalIdResolutionStatus.VerificationFailed), NormalizePublicExternalIdResolutionReason(diagnostic), mediaType, 0, 0, 0, 0, 0, null);
                return LlmExternalIdResolutionResult.VerificationFailed(diagnostic);
            }

            if (!apiResult.Success || string.IsNullOrWhiteSpace(apiResult.ContentJson))
            {
                LlmObservabilityLog.LogExternalIdResolutionCompleted(this.logger, nameof(LlmExternalIdResolutionStatus.VerificationFailed), NormalizePublicExternalIdResolutionReason(apiResult.Diagnostic), mediaType, 0, 0, 0, 0, 0, null);
                return LlmExternalIdResolutionResult.VerificationFailed(apiResult.Diagnostic);
            }

            var validationResult = this.candidateValidator.ParseAndValidateResponse(apiResult.ContentJson, request.Configuration?.LlmConfidenceThreshold ?? new PluginConfiguration().LlmConfidenceThreshold);
            if (!validationResult.Success)
            {
                LlmObservabilityLog.LogExternalIdResolutionCompleted(this.logger, nameof(LlmExternalIdResolutionStatus.ValidationFailed), NormalizePublicExternalIdResolutionReason(validationResult.Diagnostic), mediaType, 0, 0, 0, 0, 0, null);
                return LlmExternalIdResolutionResult.ValidationFailed(validationResult.Diagnostic);
            }

            if (validationResult.Candidates.Count == 0)
            {
                var diagnostic = JoinDiagnostics(validationResult.Diagnostics, "LLM external ID response contains no candidates.");
                LlmObservabilityLog.LogExternalIdResolutionCompleted(this.logger, nameof(LlmExternalIdResolutionStatus.Skipped), NormalizePublicExternalIdResolutionReason(diagnostic), mediaType, 0, 0, 0, 0, 0, null);
                return LlmExternalIdResolutionResult.Skipped(diagnostic);
            }

            var verifiedCandidates = new List<LlmExternalIdCandidate>();
            var diagnostics = new List<string>(validationResult.Diagnostics);
            foreach (var candidate in validationResult.Candidates)
            {
                LlmExternalIdVerificationResult verification;
                try
                {
                    verification = await this.VerifyCandidateAsync(candidate, request.LookupInfo, mediaType, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"{candidate.Provider} {candidate.MediaType} {candidate.Id}: verification failed: {ex.GetType().Name}");
                    continue;
                }

                if (verification.Success)
                {
                    verifiedCandidates.AddRange(verification.VerifiedCandidates);
                }
                else
                {
                    diagnostics.Add(verification.Diagnostic);
                }
            }

            if (verifiedCandidates.Count == 0)
            {
                var diagnostic = JoinDiagnostics(diagnostics, "No LLM external ID candidates could be verified.");
                LlmObservabilityLog.LogExternalIdResolutionCompleted(this.logger, nameof(LlmExternalIdResolutionStatus.VerificationFailed), NormalizePublicExternalIdResolutionReason(diagnostic), mediaType, validationResult.Candidates.Count, 0, 0, 0, 0, SummarizeExternalIdCandidates(validationResult.Candidates));
                return LlmExternalIdResolutionResult.VerificationFailed(diagnostic);
            }

            var distinctVerifiedCandidates = DistinctCandidates(verifiedCandidates);
            if (HasAmbiguousCandidates(distinctVerifiedCandidates, out var ambiguousDiagnostic))
            {
                LlmObservabilityLog.LogExternalIdResolutionCompleted(this.logger, nameof(LlmExternalIdResolutionStatus.Rejected), NormalizePublicExternalIdResolutionReason(ambiguousDiagnostic), mediaType, validationResult.Candidates.Count, distinctVerifiedCandidates.Length, 0, 0, 0, SummarizeExternalIdCandidates(distinctVerifiedCandidates));
                return LlmExternalIdResolutionResult.Rejected(ambiguousDiagnostic);
            }

            var plannedWrites = distinctVerifiedCandidates
                .Select(candidate => CreateProviderIdWrite(candidate, mediaType))
                .Where(write => write != null)
                .Select(write => write!)
                .ToArray();
            if (plannedWrites.Length == 0)
            {
                LlmObservabilityLog.LogExternalIdResolutionCompleted(this.logger, nameof(LlmExternalIdResolutionStatus.Skipped), "NoSafeProviderIdWrite", mediaType, validationResult.Candidates.Count, distinctVerifiedCandidates.Length, 0, 0, 0, SummarizeExternalIdCandidates(distinctVerifiedCandidates));
                return LlmExternalIdResolutionResult.Skipped("Verified candidates are not safe to write for this media type.", distinctVerifiedCandidates, Array.Empty<LlmExternalIdProviderIdWrite>());
            }

            var providerIds = GetProviderIds(request.LookupInfo);
            var applyResult = ApplyMissingProviderIds(providerIds, plannedWrites);
            diagnostics.AddRange(applyResult.Diagnostics);

            var status = applyResult.AppliedWrites.Count == 0
                ? LlmExternalIdResolutionStatus.Skipped
                : LlmExternalIdResolutionStatus.Succeeded;
            var finalDiagnostic = applyResult.AppliedWrites.Count == 0
                ? JoinDiagnostics(diagnostics, "All verified ProviderIds already exist.")
                : JoinDiagnostics(diagnostics, "Succeeded");
            LlmObservabilityLog.LogExternalIdResolutionCompleted(
                this.logger,
                status.ToString(),
                NormalizePublicExternalIdResolutionReason(finalDiagnostic),
                mediaType,
                validationResult.Candidates.Count,
                distinctVerifiedCandidates.Length,
                plannedWrites.Length,
                applyResult.AppliedWrites.Count,
                applyResult.SkippedWrites.Count,
                SummarizeExternalIdCandidates(distinctVerifiedCandidates));

            return applyResult.AppliedWrites.Count == 0
                ? LlmExternalIdResolutionResult.Skipped(finalDiagnostic, distinctVerifiedCandidates, applyResult.SkippedWrites)
                : LlmExternalIdResolutionResult.Succeeded(distinctVerifiedCandidates, applyResult.AppliedWrites, applyResult.SkippedWrites, finalDiagnostic);
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "TMDb correction must fail closed and preserve existing ProviderIds.")]
        public async Task<LlmTmdbIdCorrectionResult> TryResolveTmdbCorrectionAsync(LlmTmdbIdCorrectionRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.LookupInfo == null)
            {
                return this.RejectTmdbCorrection("LookupInfoMissing", request.MediaType, request.Semantic, request.IsImageProvider);
            }

            var mediaType = NormalizeMediaType(request.MediaType ?? GetMediaTypeFromLookupInfo(request.LookupInfo));
            var triggerDecision = this.tmdbCorrectionTriggerPolicy.Evaluate(new LlmAssistTriggerContext
            {
                Configuration = request.Configuration,
                Semantic = request.Semantic,
                MediaType = mediaType,
                IsImageProvider = request.IsImageProvider,
                HttpContext = request.HttpContext,
                HasBridgedExplicitSearchMissingMetadataRefreshIntent = request.HasBridgedExplicitSearchMissingMetadataRefreshIntent,
            });
            if (!triggerDecision.ShouldTrigger)
            {
                return this.RejectTmdbCorrection(triggerDecision.Reason, mediaType, request.Semantic, request.IsImageProvider);
            }

            if (!IsTmdbCorrectionSupportedMediaType(mediaType))
            {
                return this.RejectTmdbCorrection("UnsupportedMediaType", mediaType, request.Semantic, request.IsImageProvider);
            }

            var providerIds = GetProviderIds(request.LookupInfo);
            var oldTmdbId = NormalizeProviderIdValue(request.OldTmdbId);
            if (string.IsNullOrWhiteSpace(oldTmdbId) && providerIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var existingTmdbId))
            {
                oldTmdbId = NormalizeProviderIdValue(existingTmdbId);
            }

            if (!TryParsePositiveInt(oldTmdbId, out var oldTmdbNumericId))
            {
                return this.RejectTmdbCorrection("OldTmdbMissingOrInvalid", mediaType, request.Semantic, request.IsImageProvider);
            }

            var allowRelativePathContext = request.Configuration?.LlmAllowRelativePathContext ?? true;
            var prompt = LlmPromptContextBuilder.BuildExternalIdPromptJson(request.LookupInfo, mediaType, request.LibraryRoots, request.RelativePathSamples, allowRelativePathContext);
            LlmApiResult apiResult;
            try
            {
                using var lease = await this.requestLimiter.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
                if (lease == null)
                {
                    return this.RejectTmdbCorrection("LlmRequestLimiterBusy", mediaType, request.Semantic, request.IsImageProvider);
                }

                apiResult = await this.llmApi.CompleteAsync(prompt, LlmResponseSchemaKind.ExternalIdCandidates, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return this.RejectTmdbCorrection($"LLM TMDb correction request failed: {ex.GetType().Name}", mediaType, request.Semantic, request.IsImageProvider);
            }

            if (!apiResult.Success || string.IsNullOrWhiteSpace(apiResult.ContentJson))
            {
                return this.RejectTmdbCorrection(apiResult.Diagnostic, mediaType, request.Semantic, request.IsImageProvider);
            }

            var validationResult = this.candidateValidator.ParseAndValidateResponse(apiResult.ContentJson, request.Configuration?.LlmConfidenceThreshold ?? new PluginConfiguration().LlmConfidenceThreshold);
            if (!validationResult.Success)
            {
                return this.RejectTmdbCorrection(validationResult.Diagnostic, mediaType, request.Semantic, request.IsImageProvider);
            }

            var tmdbCandidates = validationResult.Candidates
                .Where(candidate => string.Equals(candidate.Provider, TmdbProvider, StringComparison.Ordinal)
                    && string.Equals(candidate.MediaType, mediaType, StringComparison.Ordinal))
                .ToArray();
            if (tmdbCandidates.Length == 0)
            {
                return this.RejectTmdbCorrection(JoinDiagnostics(validationResult.Diagnostics, "LLM TMDb correction response contains no TMDb candidate."), mediaType, request.Semantic, request.IsImageProvider);
            }

            var candidateIds = tmdbCandidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (candidateIds.Length > 1)
            {
                return this.RejectTmdbCorrection("AmbiguousCandidates: LLM TMDb correction candidates conflict.", mediaType, request.Semantic, request.IsImageProvider);
            }

            var candidate = tmdbCandidates[0];
            if (!TryParsePositiveInt(candidate.Id, out var candidateTmdbId))
            {
                return this.RejectTmdbCorrection("CandidateTmdbInvalid", mediaType, request.Semantic, request.IsImageProvider);
            }

            LlmExternalIdVerificationResult newTmdbVerified;
            try
            {
                newTmdbVerified = await this.VerifyTmdbCandidateAsync(candidate, request.LookupInfo, mediaType, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return this.RejectTmdbCorrection($"TMDb correction candidate verification failed: {ex.GetType().Name}", mediaType, request.Semantic, request.IsImageProvider);
            }

            if (!newTmdbVerified.Success)
            {
                return this.RejectTmdbCorrection(newTmdbVerified.Diagnostic, mediaType, request.Semantic, request.IsImageProvider);
            }

            if (candidateTmdbId == oldTmdbNumericId)
            {
                LlmObservabilityLog.LogTmdbCorrectionApplied(this.logger, "ExistingTmdbVerified", mediaType);
                return LlmTmdbIdCorrectionResult.VerifiedExistingTmdb(candidate.Id!, "ExistingTmdbVerified");
            }

            TmdbCorrectionEvidenceResult strongEvidence;
            try
            {
                strongEvidence = await this.FindStrongTmdbCorrectionEvidenceAsync(providerIds, mediaType, request.LookupInfo.MetadataLanguage, candidateTmdbId, oldTmdbNumericId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return this.RejectTmdbCorrection($"TMDb correction evidence verification failed: {ex.GetType().Name}", mediaType, request.Semantic, request.IsImageProvider);
            }

            if (strongEvidence.Success)
            {
                LlmObservabilityLog.LogTmdbCorrectionApplied(this.logger, NormalizePublicTmdbCorrectionReason(strongEvidence.Diagnostic), mediaType);
                return LlmTmdbIdCorrectionResult.Verified(candidate.Id!, strongEvidence.Diagnostic);
            }

            LlmObservabilityLog.LogTmdbCorrectionRejected(this.logger, NormalizePublicTmdbCorrectionReason(strongEvidence.Diagnostic), mediaType, request.Semantic, request.IsImageProvider);
            return LlmTmdbIdCorrectionResult.NoReplacement(strongEvidence.Diagnostic);
        }

        public static LlmExternalIdProviderIdApplyResult ApplyMissingProviderIds(IDictionary<string, string> providerIds, IEnumerable<LlmExternalIdProviderIdWrite> writes)
        {
            ArgumentNullException.ThrowIfNull(providerIds);
            ArgumentNullException.ThrowIfNull(writes);

            var applied = new List<LlmExternalIdProviderIdWrite>();
            var skipped = new List<LlmExternalIdProviderIdWrite>();
            var diagnostics = new List<string>();
            foreach (var write in writes)
            {
                if (providerIds.TryGetValue(write.ProviderIdKey, out var existingValue) && !string.IsNullOrWhiteSpace(existingValue))
                {
                    skipped.Add(write);
                    diagnostics.Add($"ProviderId '{write.ProviderIdKey}' already exists and was not overwritten.");
                    continue;
                }

                providerIds[write.ProviderIdKey] = write.ProviderIdValue;
                applied.Add(write);
            }

            return new LlmExternalIdProviderIdApplyResult(applied, skipped, diagnostics);
        }

        private LlmTmdbIdCorrectionResult RejectTmdbCorrection(string diagnostic, string? mediaType, DefaultScraperSemantic semantic, bool isImageProvider)
        {
            LlmObservabilityLog.LogTmdbCorrectionRejected(this.logger, NormalizePublicTmdbCorrectionReason(diagnostic), mediaType, semantic, isImageProvider);
            return LlmTmdbIdCorrectionResult.NoReplacement(diagnostic);
        }

        private static string NormalizePublicTmdbCorrectionReason(string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(diagnostic))
            {
                return "Unknown";
            }

            var trimmed = diagnostic.Trim();
            if (trimmed.Contains("AmbiguousCandidates", StringComparison.Ordinal))
            {
                return "AmbiguousCandidates";
            }

            if (trimmed.Contains("candidate verification failed", StringComparison.OrdinalIgnoreCase))
            {
                return "CandidateVerificationFailed";
            }

            if (trimmed.Contains("TMDb movie detail was not found", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("TMDb series detail was not found", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("TMDb parent series detail was not found", StringComparison.OrdinalIgnoreCase))
            {
                return "CandidateVerificationFailed";
            }

            if (trimmed.Contains("evidence verification failed", StringComparison.OrdinalIgnoreCase))
            {
                return "EvidenceVerificationFailed";
            }

            if (trimmed.Contains("no TMDb candidate", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("no candidates", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("below threshold", StringComparison.OrdinalIgnoreCase))
            {
                return "NoTmdbCandidate";
            }

            return LlmObservabilityLog.NormalizeReasonCode(trimmed);
        }

        private static string NormalizePublicExternalIdResolutionReason(string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(diagnostic))
            {
                return "Unknown";
            }

            var trimmed = diagnostic.Trim();
            if (string.Equals(trimmed, "Succeeded", StringComparison.Ordinal))
            {
                return "Succeeded";
            }

            if (trimmed.Contains("no candidates", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("contains no candidates", StringComparison.OrdinalIgnoreCase))
            {
                return "NoCandidates";
            }

            if (trimmed.Contains("schema invalid", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("field", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("below threshold", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("validation", StringComparison.OrdinalIgnoreCase))
            {
                return "CandidateValidationFailed";
            }

            if (trimmed.Contains("AmbiguousCandidates", StringComparison.Ordinal))
            {
                return "AmbiguousCandidates";
            }

            if (trimmed.Contains("not safe to write", StringComparison.OrdinalIgnoreCase))
            {
                return "NoSafeProviderIdWrite";
            }

            if (trimmed.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("not overwritten", StringComparison.OrdinalIgnoreCase))
            {
                return "ProviderIdAlreadyPresent";
            }

            if (trimmed.Contains("could be verified", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("verification failed", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("detail was not found", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("could not be confirmed", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("ownership cannot be verified", StringComparison.OrdinalIgnoreCase))
            {
                return "CandidateVerificationFailed";
            }

            return LlmObservabilityLog.NormalizeReasonCode(trimmed);
        }

        private static string SummarizeExternalIdCandidates(IEnumerable<LlmExternalIdCandidate> candidates)
        {
            var summary = string.Join(
                ",",
                candidates
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Provider)
                        && !string.IsNullOrWhiteSpace(candidate.MediaType)
                        && !string.IsNullOrWhiteSpace(candidate.Id))
                    .Select(candidate => candidate.Provider + ":" + candidate.MediaType + ":" + candidate.Id)
                    .Take(5));
            return string.IsNullOrWhiteSpace(summary) ? "none" : summary;
        }

        private static string GetMediaTypeFromLookupInfo(ItemLookupInfo lookupInfo)
        {
            return lookupInfo switch
            {
                MovieInfo => MovieMediaType,
                SeriesInfo => SeriesMediaType,
                SeasonInfo => SeasonMediaType,
                EpisodeInfo => EpisodeMediaType,
                _ => string.Empty,
            };
        }

        private static Dictionary<string, string> GetProviderIds(ItemLookupInfo lookupInfo)
        {
            return lookupInfo.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeMediaType(string? mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return string.Empty;
            }

            return mediaType.Trim().ToUpperInvariant() switch
            {
                "MOVIE" => MovieMediaType,
                "SERIES" => SeriesMediaType,
                "SEASON" => SeasonMediaType,
                "EPISODE" => EpisodeMediaType,
                _ => mediaType.Trim(),
            };
        }

        private static bool IsTmdbCorrectionSupportedMediaType(string mediaType)
        {
            return string.Equals(mediaType, MovieMediaType, StringComparison.Ordinal)
                || string.Equals(mediaType, SeriesMediaType, StringComparison.Ordinal);
        }

        private static IEnumerable<ExistingProviderIdReference> EnumerateExistingProviderIds(ItemLookupInfo lookupInfo, string targetMediaType)
        {
            foreach (var providerId in EnumerateExistingProviderIdsFromDictionary(lookupInfo.ProviderIds, targetMediaType))
            {
                yield return providerId;
            }

            if (lookupInfo is EpisodeInfo episodeInfo)
            {
                foreach (var providerId in EnumerateExistingProviderIdsFromDictionary(episodeInfo.SeriesProviderIds, SeriesMediaType))
                {
                    yield return providerId;
                }
            }
        }

        private static IEnumerable<ExistingProviderIdReference> EnumerateExistingProviderIdsFromDictionary(Dictionary<string, string>? providerIds, string semanticMediaType)
        {
            if (providerIds == null)
            {
                yield break;
            }

            foreach (var providerId in providerIds)
            {
                if (string.IsNullOrWhiteSpace(providerId.Value))
                {
                    continue;
                }

                if (string.Equals(providerId.Key, MetadataProvider.Tmdb.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ExistingProviderIdReference(TmdbProvider, providerId.Value, semanticMediaType);
                    continue;
                }

                if (string.Equals(providerId.Key, MetadataProvider.Imdb.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ExistingProviderIdReference(ImdbProvider, providerId.Value, semanticMediaType);
                    continue;
                }

                if (string.Equals(providerId.Key, MetadataProvider.Tvdb.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ExistingProviderIdReference(TvdbProvider, providerId.Value, semanticMediaType);
                    continue;
                }

                if (string.Equals(providerId.Key, BaseProvider.DoubanProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ExistingProviderIdReference(DoubanProvider, providerId.Value, semanticMediaType);
                }
            }
        }

        private static string? NormalizeProviderIdValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static int CountMovieMappings(FindContainer find)
        {
            return find.MovieResults?.Count ?? 0;
        }

        private static int CountSeriesMappings(FindContainer find)
        {
            return (find.TvResults?.Count ?? 0) + (find.TvEpisode?.Count ?? 0) + (find.TvSeason?.Count ?? 0);
        }

        private static bool IsUniqueTmdbMapping(FindContainer? find, string mediaType, int expectedTmdbId)
        {
            if (find == null)
            {
                return false;
            }

            if (string.Equals(mediaType, MovieMediaType, StringComparison.Ordinal))
            {
                return CountMovieMappings(find) == 1
                    && CountSeriesMappings(find) == 0
                    && find.MovieResults![0].Id == expectedTmdbId;
            }

            return CountMovieMappings(find) == 0
                && CountSeriesMappings(find) == 1
                && ((find.TvResults?.Count == 1 && find.TvResults[0].Id == expectedTmdbId)
                    || (find.TvEpisode?.Count == 1 && find.TvEpisode[0].ShowId == expectedTmdbId)
                    || (find.TvSeason?.Count == 1 && find.TvSeason[0].ShowId == expectedTmdbId));
        }

        private static string ProviderIdKeyForProvider(string provider)
        {
            return provider switch
            {
                TmdbProvider => MetadataProvider.Tmdb.ToString(),
                ImdbProvider => MetadataProvider.Imdb.ToString(),
                TvdbProvider => MetadataProvider.Tvdb.ToString(),
                DoubanProvider => BaseProvider.DoubanProviderId,
                _ => provider,
            };
        }

        private static LlmExternalIdProviderIdWrite? CreateProviderIdWrite(LlmExternalIdCandidate candidate, string targetMediaType)
        {
            if (!IsDirectWriteAllowed(candidate, targetMediaType))
            {
                return null;
            }

            return new LlmExternalIdProviderIdWrite(
                ProviderIdKeyForProvider(candidate.Provider!),
                candidate.Provider!,
                candidate.Id!,
                candidate.MediaType!,
                candidate);
        }

        private static bool IsDirectWriteAllowed(LlmExternalIdCandidate candidate, string targetMediaType)
        {
            return !string.Equals(targetMediaType, SeasonMediaType, StringComparison.Ordinal)
                && string.Equals(candidate.MediaType, targetMediaType, StringComparison.Ordinal)
                && (!string.Equals(candidate.Provider, TvdbProvider, StringComparison.Ordinal)
                    || string.Equals(targetMediaType, EpisodeMediaType, StringComparison.Ordinal));
        }

        private static LlmExternalIdCandidate[] DistinctCandidates(IEnumerable<LlmExternalIdCandidate> candidates)
        {
            return candidates
                .GroupBy(candidate => ProviderIdKeyForProvider(candidate.Provider!) + "\u001f" + candidate.Id + "\u001f" + candidate.MediaType, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }

        private static bool HasAmbiguousCandidates(IReadOnlyList<LlmExternalIdCandidate> candidates, out string diagnostic)
        {
            foreach (var group in candidates.GroupBy(candidate => ProviderIdKeyForProvider(candidate.Provider!) + "\u001f" + candidate.MediaType, StringComparer.Ordinal))
            {
                var values = group.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).ToArray();
                if (values.Length > 1)
                {
                    diagnostic = $"AmbiguousCandidates: verified external ID candidates conflict for {group.First().Provider} {group.First().MediaType}.";
                    return true;
                }
            }

            diagnostic = string.Empty;
            return false;
        }

        private static bool TryParsePositiveInt(string? value, out int id)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        private static string JoinDiagnostics(IEnumerable<string> diagnostics, string fallback)
        {
            var joined = string.Join(" ", diagnostics.Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic)).Distinct(StringComparer.Ordinal));
            return string.IsNullOrWhiteSpace(joined) ? fallback : joined;
        }

        private async Task<ExistingProviderIdAssessment> AssessExistingProviderIdAsync(ExistingProviderIdReference existingProviderId, ItemLookupInfo lookupInfo, string targetMediaType, LlmPromptContext localContext, CancellationToken cancellationToken)
        {
            if (string.Equals(existingProviderId.Provider, TmdbProvider, StringComparison.Ordinal))
            {
                return await this.AssessExistingTmdbProviderIdAsync(existingProviderId, lookupInfo, targetMediaType, localContext, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(existingProviderId.Provider, ImdbProvider, StringComparison.Ordinal))
            {
                return await this.AssessExistingMappedProviderIdAsync(existingProviderId, FindExternalSource.Imdb, localContext, lookupInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(existingProviderId.Provider, TvdbProvider, StringComparison.Ordinal))
            {
                if (string.Equals(existingProviderId.MediaType, EpisodeMediaType, StringComparison.Ordinal))
                {
                    var verification = await this.VerifyCandidateAsync(CreateCandidate(TvdbProvider, existingProviderId.Id, EpisodeMediaType), lookupInfo, targetMediaType, cancellationToken).ConfigureAwait(false);
                    return verification.Success ? ExistingProviderIdAssessment.Consistent : ExistingProviderIdAssessment.Conflict;
                }

                return await this.AssessExistingMappedProviderIdAsync(existingProviderId, FindExternalSource.TvDb, localContext, lookupInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(existingProviderId.Provider, DoubanProvider, StringComparison.Ordinal))
            {
                return await this.AssessExistingDoubanProviderIdAsync(existingProviderId, localContext, cancellationToken).ConfigureAwait(false);
            }

            return ExistingProviderIdAssessment.Inconclusive;
        }

        private async Task<ExistingProviderIdAssessment> AssessExistingTmdbProviderIdAsync(ExistingProviderIdReference existingProviderId, ItemLookupInfo lookupInfo, string targetMediaType, LlmPromptContext localContext, CancellationToken cancellationToken)
        {
            if (string.Equals(existingProviderId.MediaType, EpisodeMediaType, StringComparison.Ordinal))
            {
                var verification = await this.VerifyCandidateAsync(CreateCandidate(TmdbProvider, existingProviderId.Id, EpisodeMediaType), lookupInfo, targetMediaType, cancellationToken).ConfigureAwait(false);
                return verification.Success ? ExistingProviderIdAssessment.Consistent : ExistingProviderIdAssessment.Conflict;
            }

            var suggestion = await this.CreateTmdbSemanticSuggestionAsync(existingProviderId.Id, existingProviderId.MediaType, lookupInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
            return suggestion == null ? ExistingProviderIdAssessment.Inconclusive : this.EvaluateSemanticSuggestion(localContext, suggestion);
        }

        private async Task<ExistingProviderIdAssessment> AssessExistingMappedProviderIdAsync(ExistingProviderIdReference existingProviderId, FindExternalSource externalSource, LlmPromptContext localContext, string language, CancellationToken cancellationToken)
        {
            var find = await this.tmdbApi.FindByExternalIdAsync(existingProviderId.Id, externalSource, language, cancellationToken).ConfigureAwait(false);
            if (!TryResolveMappedTmdbId(find, existingProviderId.MediaType, out var mappedTmdbId))
            {
                return ExistingProviderIdAssessment.Inconclusive;
            }

            var suggestion = await this.CreateTmdbSemanticSuggestionAsync(mappedTmdbId.ToString(CultureInfo.InvariantCulture), existingProviderId.MediaType, language, cancellationToken).ConfigureAwait(false);
            return suggestion == null ? ExistingProviderIdAssessment.Inconclusive : this.EvaluateSemanticSuggestion(localContext, suggestion);
        }

        private async Task<ExistingProviderIdAssessment> AssessExistingDoubanProviderIdAsync(ExistingProviderIdReference existingProviderId, LlmPromptContext localContext, CancellationToken cancellationToken)
        {
            if (!string.Equals(existingProviderId.MediaType, MovieMediaType, StringComparison.Ordinal) && !string.Equals(existingProviderId.MediaType, SeriesMediaType, StringComparison.Ordinal))
            {
                return ExistingProviderIdAssessment.Inconclusive;
            }

            DoubanSubject? subject;
            try
            {
                subject = await this.doubanApi.GetMovieAsync(existingProviderId.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return ExistingProviderIdAssessment.Inconclusive;
            }

            if (subject == null)
            {
                return ExistingProviderIdAssessment.Inconclusive;
            }

            return this.EvaluateSemanticSuggestion(localContext, new LlmScrapingSuggestion
            {
                MediaType = existingProviderId.MediaType,
                Title = subject.Name,
                OriginalTitle = subject.OriginalName,
                Year = subject.Year > 0 ? subject.Year : null,
                Confidence = 1,
            });
        }

        private async Task<LlmScrapingSuggestion?> CreateTmdbSemanticSuggestionAsync(string tmdbId, string mediaType, string language, CancellationToken cancellationToken)
        {
            if (!TryParsePositiveInt(tmdbId, out var tmdbNumericId))
            {
                return null;
            }

            if (string.Equals(mediaType, MovieMediaType, StringComparison.Ordinal))
            {
                var movie = await this.tmdbApi.GetMovieAsync(tmdbNumericId, language, string.Empty, cancellationToken).ConfigureAwait(false);
                return movie == null ? null : new LlmScrapingSuggestion
                {
                    MediaType = MovieMediaType,
                    Title = movie.Title,
                    OriginalTitle = movie.OriginalTitle,
                    Year = movie.ReleaseDate?.Year,
                    Confidence = 1,
                };
            }

            var series = await this.tmdbApi.GetSeriesAsync(tmdbNumericId, language, string.Empty, cancellationToken).ConfigureAwait(false);
            return series == null ? null : new LlmScrapingSuggestion
            {
                MediaType = SeriesMediaType,
                Title = series.Name,
                OriginalTitle = series.OriginalName,
                Year = series.FirstAirDate?.Year,
                Confidence = 1,
            };
        }

        private static bool TryResolveMappedTmdbId(FindContainer? find, string mediaType, out int tmdbId)
        {
            tmdbId = 0;
            if (find == null)
            {
                return false;
            }

            if (string.Equals(mediaType, MovieMediaType, StringComparison.Ordinal))
            {
                if (find.MovieResults?.Count == 1 && (find.TvResults == null || find.TvResults.Count == 0))
                {
                    tmdbId = find.MovieResults[0].Id;
                    return tmdbId > 0;
                }

                return false;
            }

            if (find.TvResults?.Count == 1 && (find.MovieResults == null || find.MovieResults.Count == 0))
            {
                tmdbId = find.TvResults[0].Id;
                return tmdbId > 0;
            }

            return false;
        }

        private ExistingProviderIdAssessment EvaluateSemanticSuggestion(LlmPromptContext localContext, LlmScrapingSuggestion suggestion)
        {
            var mismatch = this.mismatchDetector.Detect(localContext, suggestion);
            return mismatch.IsMismatch ? ExistingProviderIdAssessment.Conflict : ExistingProviderIdAssessment.Consistent;
        }

        private static LlmExternalIdCandidate CreateCandidate(string provider, string id, string mediaType)
        {
            return new LlmExternalIdCandidate
            {
                Provider = provider,
                Id = id,
                MediaType = mediaType,
                Confidence = 1,
                Reason = "existing provider id semantics evaluation",
                Evidence = "existing provider id semantics evaluation",
            };
        }

        private static LlmExternalIdVerificationResult VerificationFailed(string diagnostic, LlmExternalIdCandidate candidate)
        {
            return LlmExternalIdVerificationResult.Failed($"{candidate.Provider} {candidate.MediaType} {candidate.Id}: {diagnostic}");
        }

        private static LlmExternalIdVerificationResult VerificationSucceeded(params LlmExternalIdCandidate[] candidates)
        {
            return LlmExternalIdVerificationResult.Succeeded(candidates);
        }

        private static LlmExternalIdCandidate CreateDerivedCandidate(LlmExternalIdCandidate source, string provider, string id, string mediaType)
        {
            return new LlmExternalIdCandidate
            {
                Provider = provider,
                Id = id,
                MediaType = mediaType,
                Confidence = source.Confidence,
                Reason = source.Reason,
                Evidence = source.Evidence,
            };
        }

        private async Task<TmdbCorrectionEvidenceResult> FindStrongTmdbCorrectionEvidenceAsync(
            Dictionary<string, string> providerIds,
            string mediaType,
            string language,
            int candidateTmdbId,
            int oldTmdbId,
            CancellationToken cancellationToken)
        {
            if (providerIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdbId) && !string.IsNullOrWhiteSpace(imdbId))
            {
                var imdbEvidence = await this.VerifyImdbTmdbCorrectionEvidenceAsync(imdbId, mediaType, language, candidateTmdbId, oldTmdbId, cancellationToken).ConfigureAwait(false);
                if (imdbEvidence.Success)
                {
                    return imdbEvidence;
                }

                return imdbEvidence;
            }

            if (providerIds.TryGetValue(MetadataProvider.Tvdb.ToString(), out var tvdbId) && !string.IsNullOrWhiteSpace(tvdbId))
            {
                return await this.VerifyTvdbTmdbCorrectionEvidenceAsync(tvdbId, mediaType, language, candidateTmdbId, oldTmdbId, cancellationToken).ConfigureAwait(false);
            }

            if (providerIds.TryGetValue(BaseProvider.DoubanProviderId, out var doubanId) && !string.IsNullOrWhiteSpace(doubanId))
            {
                return await this.VerifyDoubanTmdbCorrectionEvidenceAsync(doubanId, mediaType, language, candidateTmdbId, oldTmdbId, cancellationToken).ConfigureAwait(false);
            }

            return TmdbCorrectionEvidenceResult.Failed("StrongEvidenceMissing");
        }

        private async Task<TmdbCorrectionEvidenceResult> VerifyImdbTmdbCorrectionEvidenceAsync(
            string imdbId,
            string mediaType,
            string language,
            int candidateTmdbId,
            int oldTmdbId,
            CancellationToken cancellationToken)
        {
            var find = await this.tmdbApi.FindByExternalIdAsync(imdbId, FindExternalSource.Imdb, language, cancellationToken).ConfigureAwait(false);
            if (IsUniqueTmdbMapping(find, mediaType, candidateTmdbId) && !IsUniqueTmdbMapping(find, mediaType, oldTmdbId))
            {
                return TmdbCorrectionEvidenceResult.Succeeded("IMDbUniqueMappingVerified");
            }

            return TmdbCorrectionEvidenceResult.Failed("ImdbEvidenceDoesNotAlign");
        }

        private async Task<TmdbCorrectionEvidenceResult> VerifyTvdbTmdbCorrectionEvidenceAsync(
            string tvdbId,
            string mediaType,
            string language,
            int candidateTmdbId,
            int oldTmdbId,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(mediaType, SeriesMediaType, StringComparison.Ordinal))
            {
                await Task.CompletedTask.ConfigureAwait(false);
                return TmdbCorrectionEvidenceResult.Failed("TvdbOwnershipUnverifiable");
            }

            var find = await this.tmdbApi.FindByExternalIdAsync(tvdbId, FindExternalSource.TvDb, language, cancellationToken).ConfigureAwait(false);
            if (IsUniqueTmdbMapping(find, mediaType, candidateTmdbId) && !IsUniqueTmdbMapping(find, mediaType, oldTmdbId))
            {
                return TmdbCorrectionEvidenceResult.Succeeded("TVDBUniqueMappingVerified");
            }

            return TmdbCorrectionEvidenceResult.Failed("TvdbOwnershipUnverifiable");
        }

        private async Task<TmdbCorrectionEvidenceResult> VerifyDoubanTmdbCorrectionEvidenceAsync(
            string doubanId,
            string mediaType,
            string language,
            int candidateTmdbId,
            int oldTmdbId,
            CancellationToken cancellationToken)
        {
            var subject = await this.doubanApi.GetMovieAsync(doubanId, cancellationToken).ConfigureAwait(false);
            var expectedCategory = string.Equals(mediaType, MovieMediaType, StringComparison.Ordinal) ? "电影" : "电视剧";
            if (subject == null
                || !string.Equals(subject.Category, expectedCategory, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(subject.Imdb))
            {
                return TmdbCorrectionEvidenceResult.Failed("DoubanOwnershipUnverifiable");
            }

            var find = await this.tmdbApi.FindByExternalIdAsync(subject.Imdb, FindExternalSource.Imdb, language, cancellationToken).ConfigureAwait(false);
            if (IsUniqueTmdbMapping(find, mediaType, candidateTmdbId) && !IsUniqueTmdbMapping(find, mediaType, oldTmdbId))
            {
                return TmdbCorrectionEvidenceResult.Succeeded("DoubanOwnershipVerified");
            }

            return TmdbCorrectionEvidenceResult.Failed("DoubanOwnershipUnverifiable");
        }

        private async Task<LlmExternalIdVerificationResult> VerifyCandidateAsync(LlmExternalIdCandidate candidate, ItemLookupInfo lookupInfo, string targetMediaType, CancellationToken cancellationToken)
        {
            if (!IsCandidateMediaTypeCompatible(candidate, targetMediaType))
            {
                return VerificationFailed("candidate media type does not match target media type", candidate);
            }

            if (string.Equals(candidate.Provider, TmdbProvider, StringComparison.Ordinal))
            {
                return await this.VerifyTmdbCandidateAsync(candidate, lookupInfo, targetMediaType, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(candidate.Provider, ImdbProvider, StringComparison.Ordinal))
            {
                return await this.VerifyImdbCandidateAsync(candidate, targetMediaType, lookupInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(candidate.Provider, DoubanProvider, StringComparison.Ordinal))
            {
                return await this.VerifyDoubanCandidateAsync(candidate, targetMediaType, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(candidate.Provider, TvdbProvider, StringComparison.Ordinal))
            {
                return await this.VerifyTvdbCandidateAsync(candidate, lookupInfo, targetMediaType, cancellationToken).ConfigureAwait(false);
            }

            return VerificationFailed("provider is not supported", candidate);
        }

        private async Task<LlmExternalIdVerificationResult> VerifyTmdbCandidateAsync(LlmExternalIdCandidate candidate, ItemLookupInfo lookupInfo, string targetMediaType, CancellationToken cancellationToken)
        {
            if (!TryParsePositiveInt(candidate.Id, out var tmdbId))
            {
                return VerificationFailed("TMDb id is invalid", candidate);
            }

            if (string.Equals(targetMediaType, MovieMediaType, StringComparison.Ordinal))
            {
                var movie = await this.tmdbApi.GetMovieAsync(tmdbId, lookupInfo.MetadataLanguage, string.Empty, cancellationToken).ConfigureAwait(false);
                return movie == null ? VerificationFailed("TMDb movie detail was not found", candidate) : VerificationSucceeded(candidate);
            }

            if (string.Equals(targetMediaType, SeriesMediaType, StringComparison.Ordinal))
            {
                var series = await this.tmdbApi.GetSeriesAsync(tmdbId, lookupInfo.MetadataLanguage, string.Empty, cancellationToken).ConfigureAwait(false);
                return series == null ? VerificationFailed("TMDb series detail was not found", candidate) : VerificationSucceeded(candidate);
            }

            if (string.Equals(targetMediaType, SeasonMediaType, StringComparison.Ordinal))
            {
                var series = await this.tmdbApi.GetSeriesAsync(tmdbId, lookupInfo.MetadataLanguage, string.Empty, cancellationToken).ConfigureAwait(false);
                return series == null ? VerificationFailed("TMDb parent series detail was not found", candidate) : VerificationSucceeded(CreateDerivedCandidate(candidate, TmdbProvider, candidate.Id!, SeriesMediaType));
            }

            if (string.Equals(targetMediaType, EpisodeMediaType, StringComparison.Ordinal))
            {
                if (string.Equals(candidate.MediaType, SeriesMediaType, StringComparison.Ordinal))
                {
                    var series = await this.tmdbApi.GetSeriesAsync(tmdbId, lookupInfo.MetadataLanguage, string.Empty, cancellationToken).ConfigureAwait(false);
                    return series == null ? VerificationFailed("TMDb parent series detail was not found", candidate) : VerificationSucceeded(CreateDerivedCandidate(candidate, TmdbProvider, candidate.Id!, SeriesMediaType));
                }

                return await this.VerifyTmdbEpisodeCandidateAsync(candidate, lookupInfo, tmdbId, cancellationToken).ConfigureAwait(false);
            }

            return VerificationFailed("target media type is unsupported", candidate);
        }

        private async Task<LlmExternalIdVerificationResult> VerifyTmdbEpisodeCandidateAsync(LlmExternalIdCandidate candidate, ItemLookupInfo lookupInfo, int candidateEpisodeId, CancellationToken cancellationToken)
        {
            if (lookupInfo is not EpisodeInfo episodeInfo)
            {
                return VerificationFailed("episode lookup info is required", candidate);
            }

            if (!TryGetProviderId(episodeInfo.SeriesProviderIds, MetadataProvider.Tmdb.ToString(), out var seriesTmdbId)
                || !TryParsePositiveInt(seriesTmdbId, out var seriesId)
                || episodeInfo.ParentIndexNumber == null
                || episodeInfo.IndexNumber == null)
            {
                return VerificationFailed("episode parent TMDb series, season, and episode numbers are required", candidate);
            }

            var episode = await this.tmdbApi.GetEpisodeAsync(seriesId, episodeInfo.ParentIndexNumber.Value, episodeInfo.IndexNumber.Value, episodeInfo.MetadataLanguage, string.Empty, cancellationToken).ConfigureAwait(false);
            return episode?.Id == candidateEpisodeId ? VerificationSucceeded(candidate) : VerificationFailed("TMDb episode detail did not match the same series, season, and episode", candidate);
        }

        private static bool IsCandidateMediaTypeCompatible(LlmExternalIdCandidate candidate, string targetMediaType)
        {
            if (string.Equals(candidate.MediaType, targetMediaType, StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(candidate.MediaType, SeriesMediaType, StringComparison.Ordinal)
                && (string.Equals(targetMediaType, SeasonMediaType, StringComparison.Ordinal)
                    || (string.Equals(targetMediaType, EpisodeMediaType, StringComparison.Ordinal) && string.Equals(candidate.Provider, TmdbProvider, StringComparison.Ordinal)));
        }

        private async Task<LlmExternalIdVerificationResult> VerifyImdbCandidateAsync(LlmExternalIdCandidate candidate, string targetMediaType, string language, CancellationToken cancellationToken)
        {
            if (!string.Equals(targetMediaType, MovieMediaType, StringComparison.Ordinal) && !string.Equals(targetMediaType, SeriesMediaType, StringComparison.Ordinal))
            {
                return VerificationFailed("IMDb write is only supported for movie and series", candidate);
            }

            var find = await this.tmdbApi.FindByExternalIdAsync(candidate.Id!, FindExternalSource.Imdb, language, cancellationToken).ConfigureAwait(false);
            if (string.Equals(targetMediaType, MovieMediaType, StringComparison.Ordinal))
            {
                return find?.MovieResults?.Count == 1 && (find.TvResults == null || find.TvResults.Count == 0)
                    ? VerificationSucceeded(candidate)
                    : VerificationFailed("IMDb id could not be confirmed as exactly one TMDb movie", candidate);
            }

            return find?.TvResults?.Count == 1 && (find.MovieResults == null || find.MovieResults.Count == 0)
                ? VerificationSucceeded(candidate)
                : VerificationFailed("IMDb id could not be confirmed as exactly one TMDb series", candidate);
        }

        private async Task<LlmExternalIdVerificationResult> VerifyDoubanCandidateAsync(LlmExternalIdCandidate candidate, string targetMediaType, CancellationToken cancellationToken)
        {
            if (!string.Equals(targetMediaType, MovieMediaType, StringComparison.Ordinal) && !string.Equals(targetMediaType, SeriesMediaType, StringComparison.Ordinal))
            {
                return VerificationFailed("Douban write is only supported for movie and series", candidate);
            }

            DoubanSubject? subject;
            try
            {
                subject = await this.doubanApi.GetMovieAsync(candidate.Id!, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                return VerificationFailed($"Douban lookup failed: {ex.GetType().Name}", candidate);
            }
            catch (TaskCanceledException ex)
            {
                return VerificationFailed($"Douban lookup failed: {ex.GetType().Name}", candidate);
            }

            if (subject == null)
            {
                return VerificationFailed("Douban subject was not found", candidate);
            }

            var expectedCategory = string.Equals(targetMediaType, MovieMediaType, StringComparison.Ordinal) ? "电影" : "电视剧";
            return string.Equals(subject.Category, expectedCategory, StringComparison.Ordinal)
                ? VerificationSucceeded(candidate)
                : VerificationFailed("Douban subject category did not match target media type", candidate);
        }

        private async Task<LlmExternalIdVerificationResult> VerifyTvdbCandidateAsync(LlmExternalIdCandidate candidate, ItemLookupInfo lookupInfo, string targetMediaType, CancellationToken cancellationToken)
        {
            if (string.Equals(targetMediaType, SeriesMediaType, StringComparison.Ordinal))
            {
                await Task.CompletedTask.ConfigureAwait(false);
                return VerificationFailed("TVDB series ownership cannot be verified by current API", candidate);
            }

            if (!string.Equals(targetMediaType, EpisodeMediaType, StringComparison.Ordinal))
            {
                await Task.CompletedTask.ConfigureAwait(false);
                return VerificationFailed("TVDB write is only supported for verified episodes", candidate);
            }

            if (lookupInfo is not EpisodeInfo episodeInfo
                || !TryParsePositiveInt(candidate.Id, out var candidateEpisodeId)
                || !TryGetProviderId(episodeInfo.SeriesProviderIds, MetadataProvider.Tvdb.ToString(), out var seriesTvdbId)
                || !TryParsePositiveInt(seriesTvdbId, out var seriesId)
                || episodeInfo.ParentIndexNumber == null
                || episodeInfo.IndexNumber == null)
            {
                return VerificationFailed("episode parent TVDB series, season, and episode numbers are required", candidate);
            }

            var episodes = await this.tvdbApi
                .GetSeriesEpisodesAsync(seriesId, "official", episodeInfo.ParentIndexNumber.Value, episodeInfo.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);
            var match = episodes.FirstOrDefault(episode => episode.Id == candidateEpisodeId);
            return match?.SeasonNumber == episodeInfo.ParentIndexNumber.Value && match.Number == episodeInfo.IndexNumber.Value
                ? VerificationSucceeded(candidate)
                : VerificationFailed("TVDB episode detail did not match the same series, season, and episode", candidate);
        }

        private static bool TryGetProviderId(Dictionary<string, string>? providerIds, string key, out string value)
        {
            value = string.Empty;
            return providerIds != null && providerIds.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value);
        }

        private sealed class TmdbCorrectionEvidenceResult
        {
            private TmdbCorrectionEvidenceResult(bool success, string diagnostic)
            {
                this.Success = success;
                this.Diagnostic = diagnostic;
            }

            public bool Success { get; }

            public string Diagnostic { get; }

            public static TmdbCorrectionEvidenceResult Succeeded(string diagnostic)
            {
                return new TmdbCorrectionEvidenceResult(true, diagnostic);
            }

            public static TmdbCorrectionEvidenceResult Failed(string diagnostic)
            {
                return new TmdbCorrectionEvidenceResult(false, diagnostic);
            }
        }

        private sealed class LlmExternalIdVerificationResult
        {
            private LlmExternalIdVerificationResult(bool success, IReadOnlyList<LlmExternalIdCandidate> verifiedCandidates, string diagnostic)
            {
                this.Success = success;
                this.VerifiedCandidates = verifiedCandidates;
                this.Diagnostic = diagnostic;
            }

            public bool Success { get; }

            public IReadOnlyList<LlmExternalIdCandidate> VerifiedCandidates { get; }

            public string Diagnostic { get; }

            public static LlmExternalIdVerificationResult Succeeded(IReadOnlyList<LlmExternalIdCandidate> verifiedCandidates)
            {
                return new LlmExternalIdVerificationResult(true, verifiedCandidates, string.Empty);
            }

            public static LlmExternalIdVerificationResult Failed(string diagnostic)
            {
                return new LlmExternalIdVerificationResult(false, Array.Empty<LlmExternalIdCandidate>(), diagnostic);
            }
        }

        private sealed class ExistingProviderIdReference
        {
            public ExistingProviderIdReference(string provider, string id, string mediaType)
            {
                this.Provider = provider;
                this.Id = id;
                this.MediaType = mediaType;
            }

            public string Provider { get; }

            public string Id { get; }

            public string MediaType { get; }
        }

        private enum ExistingProviderIdAssessment
        {
            Inconclusive = 0,
            Consistent = 1,
            Conflict = 2,
        }
    }
}
