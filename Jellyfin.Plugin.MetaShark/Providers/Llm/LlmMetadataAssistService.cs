// <copyright file="LlmMetadataAssistService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Api;
    using Microsoft.Extensions.Logging;

    public sealed class LlmMetadataAssistService : ILlmMetadataAssistService
    {
        private readonly ILlmApi llmApi;
        private readonly LlmAssistTriggerPolicy triggerPolicy;
        private readonly LlmScrapeContextBuilder contextBuilder;
        private readonly LlmSuggestionValidator suggestionValidator;
        private readonly LlmScrapeMismatchDetector mismatchDetector;
        private readonly LlmMetadataMergePolicy mergePolicy;
        private readonly ILlmRequestLimiter requestLimiter;
        private readonly ILogger<LlmMetadataAssistService>? logger;

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Compatibility constructor owns a process-local fallback limiter for test-only direct construction.")]
        public LlmMetadataAssistService(ILlmApi llmApi)
            : this(llmApi, new LlmAssistTriggerPolicy(), new LlmScrapeContextBuilder(), new LlmSuggestionValidator(), new LlmScrapeMismatchDetector(), new LlmMetadataMergePolicy(), new LlmRequestLimiter())
        {
        }

        public LlmMetadataAssistService(
            ILlmApi llmApi,
            LlmAssistTriggerPolicy triggerPolicy,
            LlmScrapeContextBuilder contextBuilder,
            LlmSuggestionValidator suggestionValidator,
            LlmScrapeMismatchDetector mismatchDetector,
            LlmMetadataMergePolicy mergePolicy,
            ILlmRequestLimiter? requestLimiter = null,
            ILogger<LlmMetadataAssistService>? logger = null)
        {
            this.llmApi = llmApi ?? throw new ArgumentNullException(nameof(llmApi));
            this.triggerPolicy = triggerPolicy ?? throw new ArgumentNullException(nameof(triggerPolicy));
            this.contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
            this.suggestionValidator = suggestionValidator ?? throw new ArgumentNullException(nameof(suggestionValidator));
            this.mismatchDetector = mismatchDetector ?? throw new ArgumentNullException(nameof(mismatchDetector));
            this.mergePolicy = mergePolicy ?? throw new ArgumentNullException(nameof(mergePolicy));
            this.requestLimiter = requestLimiter ?? new LlmRequestLimiter();
            this.logger = logger;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "LLM assist must fail silently and let metadata scraping continue.")]
        public async Task<LlmScrapingAssistResult> AssistAsync(LlmScrapingAssistRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.LookupInfo == null)
            {
                LlmObservabilityLog.LogLlmAssistRejected(this.logger, "LookupInfoMissing", request.MediaType, request.Semantic, request.IsImageProvider);
                return LlmScrapingAssistResult.NotTriggered("LookupInfoMissing");
            }

            var triggerDecision = this.triggerPolicy.Evaluate(new LlmAssistTriggerContext
            {
                Configuration = request.Configuration,
                Semantic = request.Semantic,
                MediaType = request.MediaType,
                IsImageProvider = request.IsImageProvider,
                HttpContext = request.HttpContext,
            });
            if (!triggerDecision.ShouldTrigger)
            {
                return LlmScrapingAssistResult.NotTriggered(triggerDecision.Reason);
            }

            var mediaType = request.MediaType ?? string.Empty;
            var allowRelativePathContext = request.Configuration?.LlmAllowRelativePathContext ?? true;
            var context = this.contextBuilder.Build(request.LookupInfo, mediaType, request.LibraryRoots, allowRelativePathContext);
            var prompt = this.contextBuilder.BuildJson(request.LookupInfo, mediaType, request.LibraryRoots, allowRelativePathContext);
            LlmApiResult apiResult;
            try
            {
                using var lease = await this.requestLimiter.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
                if (lease == null)
                {
                    LlmObservabilityLog.LogLlmAssistRejected(this.logger, "LlmRequestLimiterBusy", mediaType, request.Semantic, request.IsImageProvider);
                    return LlmScrapingAssistResult.Skipped("LlmRequestLimiterBusy", context);
                }

                apiResult = await this.llmApi.CompleteAsync(prompt, LlmResponseSchemaKind.MetadataSuggestions, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return LlmScrapingAssistResult.Failed($"LLM assist failed: {ex.GetType().Name}", context);
            }

            if (!apiResult.Success || string.IsNullOrWhiteSpace(apiResult.ContentJson))
            {
                return LlmScrapingAssistResult.Failed(apiResult.Diagnostic, context);
            }

            var validationResult = this.suggestionValidator.ParseAndValidate(apiResult.ContentJson, request.Configuration?.LlmConfidenceThreshold ?? 0.75);
            if (!validationResult.Success)
            {
                return LlmScrapingAssistResult.Failed(validationResult.Diagnostic, context);
            }

            if (validationResult.Suggestion == null)
            {
                return LlmScrapingAssistResult.Skipped(validationResult.Diagnostic, context);
            }

            var mismatch = this.mismatchDetector.Detect(context, validationResult.Suggestion);
            if (mismatch.IsMismatch)
            {
                return LlmScrapingAssistResult.Failed(mismatch.Reason, context);
            }

            return LlmScrapingAssistResult.Succeeded(context, validationResult.Suggestion, this.mergePolicy.CreateSearchHints(validationResult.Suggestion, request.Configuration));
        }
    }
}
