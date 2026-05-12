// <copyright file="ILlmExternalIdResolutionService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface ILlmExternalIdResolutionService
    {
        Task<LlmAssistTriggerDecision> EvaluateExistingProviderIdsAsync(LlmExternalIdResolutionRequest request, CancellationToken cancellationToken);

        Task<LlmExternalIdResolutionResult> ResolveAsync(LlmExternalIdResolutionRequest request, CancellationToken cancellationToken);

        Task<LlmTmdbIdCorrectionResult> TryResolveTmdbCorrectionAsync(LlmTmdbIdCorrectionRequest request, CancellationToken cancellationToken);
    }
}
