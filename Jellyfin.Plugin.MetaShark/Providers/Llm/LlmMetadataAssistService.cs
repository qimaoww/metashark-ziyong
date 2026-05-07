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

    public sealed class LlmMetadataAssistService : ILlmMetadataAssistService
    {
        private readonly ILlmApi llmApi;
        private readonly LlmAssistTriggerPolicy triggerPolicy;
        private readonly LlmScrapeContextBuilder contextBuilder;
        private readonly LlmSuggestionValidator suggestionValidator;
        private readonly LlmScrapeMismatchDetector mismatchDetector;
        private readonly LlmMetadataMergePolicy mergePolicy;

        public LlmMetadataAssistService(ILlmApi llmApi)
            : this(llmApi, new LlmAssistTriggerPolicy(), new LlmScrapeContextBuilder(), new LlmSuggestionValidator(), new LlmScrapeMismatchDetector(), new LlmMetadataMergePolicy())
        {
        }

        public LlmMetadataAssistService(
            ILlmApi llmApi,
            LlmAssistTriggerPolicy triggerPolicy,
            LlmScrapeContextBuilder contextBuilder,
            LlmSuggestionValidator suggestionValidator,
            LlmScrapeMismatchDetector mismatchDetector,
            LlmMetadataMergePolicy mergePolicy)
        {
            this.llmApi = llmApi ?? throw new ArgumentNullException(nameof(llmApi));
            this.triggerPolicy = triggerPolicy ?? throw new ArgumentNullException(nameof(triggerPolicy));
            this.contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
            this.suggestionValidator = suggestionValidator ?? throw new ArgumentNullException(nameof(suggestionValidator));
            this.mismatchDetector = mismatchDetector ?? throw new ArgumentNullException(nameof(mismatchDetector));
            this.mergePolicy = mergePolicy ?? throw new ArgumentNullException(nameof(mergePolicy));
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "LLM assist must fail silently and let metadata scraping continue.")]
        public async Task<LlmScrapingAssistResult> AssistAsync(LlmScrapingAssistRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.LookupInfo == null)
            {
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
            var context = this.contextBuilder.Build(request.LookupInfo, mediaType, request.LibraryRoots);
            var prompt = this.contextBuilder.BuildJson(request.LookupInfo, mediaType, request.LibraryRoots);
            LlmApiResult apiResult;
            try
            {
                apiResult = await this.llmApi.CompleteAsync(prompt, cancellationToken).ConfigureAwait(false);
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
            if (!validationResult.Success || validationResult.Suggestion == null)
            {
                return LlmScrapingAssistResult.Failed(validationResult.Diagnostic, context);
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
