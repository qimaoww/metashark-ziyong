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
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using TMDbLib.Objects.Find;
    using TMDbLib.Objects.TvShows;

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
        private readonly LlmExternalIdCandidateValidator candidateValidator;
        private readonly ILlmRequestLimiter requestLimiter;

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
        {
            this.llmApi = llmApi ?? throw new ArgumentNullException(nameof(llmApi));
            this.tmdbApi = tmdbApi ?? throw new ArgumentNullException(nameof(tmdbApi));
            this.doubanApi = doubanApi ?? throw new ArgumentNullException(nameof(doubanApi));
            this.tvdbApi = tvdbApi ?? throw new ArgumentNullException(nameof(tvdbApi));
            this.triggerPolicy = triggerPolicy ?? throw new ArgumentNullException(nameof(triggerPolicy));
            this.candidateValidator = candidateValidator ?? throw new ArgumentNullException(nameof(candidateValidator));
            this.requestLimiter = requestLimiter ?? new LlmRequestLimiter();
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "LLM external ID resolution must fail closed and preserve ProviderIds.")]
        public async Task<LlmExternalIdResolutionResult> ResolveAsync(LlmExternalIdResolutionRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.LookupInfo == null)
            {
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
            });
            if (!triggerDecision.ShouldTrigger)
            {
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
                return LlmExternalIdResolutionResult.VerificationFailed($"LLM external ID request failed: {ex.GetType().Name}");
            }

            if (!apiResult.Success || string.IsNullOrWhiteSpace(apiResult.ContentJson))
            {
                return LlmExternalIdResolutionResult.VerificationFailed(apiResult.Diagnostic);
            }

            var validationResult = this.candidateValidator.ParseAndValidateResponse(apiResult.ContentJson, request.Configuration?.LlmConfidenceThreshold ?? new PluginConfiguration().LlmConfidenceThreshold);
            if (!validationResult.Success)
            {
                return LlmExternalIdResolutionResult.ValidationFailed(validationResult.Diagnostic);
            }

            if (validationResult.Candidates.Count == 0)
            {
                return LlmExternalIdResolutionResult.Skipped(JoinDiagnostics(validationResult.Diagnostics, "LLM external ID response contains no candidates."));
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
                return LlmExternalIdResolutionResult.VerificationFailed(JoinDiagnostics(diagnostics, "No LLM external ID candidates could be verified."));
            }

            var distinctVerifiedCandidates = DistinctCandidates(verifiedCandidates);
            if (HasAmbiguousCandidates(distinctVerifiedCandidates, out var ambiguousDiagnostic))
            {
                return LlmExternalIdResolutionResult.Rejected(ambiguousDiagnostic);
            }

            var plannedWrites = distinctVerifiedCandidates
                .Select(candidate => CreateProviderIdWrite(candidate, mediaType))
                .Where(write => write != null)
                .Select(write => write!)
                .ToArray();
            if (plannedWrites.Length == 0)
            {
                return LlmExternalIdResolutionResult.Skipped("Verified candidates are not safe to write for this media type.", distinctVerifiedCandidates, Array.Empty<LlmExternalIdProviderIdWrite>());
            }

            var providerIds = GetProviderIds(request.LookupInfo);
            var applyResult = ApplyMissingProviderIds(providerIds, plannedWrites);
            diagnostics.AddRange(applyResult.Diagnostics);

            return applyResult.AppliedWrites.Count == 0
                ? LlmExternalIdResolutionResult.Skipped(JoinDiagnostics(diagnostics, "All verified ProviderIds already exist."), distinctVerifiedCandidates, applyResult.SkippedWrites)
                : LlmExternalIdResolutionResult.Succeeded(distinctVerifiedCandidates, applyResult.AppliedWrites, applyResult.SkippedWrites, JoinDiagnostics(diagnostics, "Succeeded"));
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
    }
}
