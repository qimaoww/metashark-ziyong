// <copyright file="LlmEpisodeGroupMappingProviderAssistService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Providers.Llm;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Objects.TvShows;

    public sealed class LlmEpisodeGroupMappingProviderAssistService : ILlmEpisodeGroupMappingProviderAssistService
    {
        private static readonly Action<ILogger, int, Exception?> LogQueuedRefresh =
            LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, nameof(LlmEpisodeGroupMappingProviderAssistService)), "[MetaShark] LLM 剧集组映射变更已排队刷新. Count={Count}.");

        private readonly ILlmEpisodeGroupMappingAssistService assistService;
        private readonly TmdbApi tmdbApi;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly LlmAssistTriggerPolicy triggerPolicy;
        private readonly EpisodeGroupRefreshService refreshService;
        private readonly ILogger<LlmEpisodeGroupMappingProviderAssistService> logger;

        public LlmEpisodeGroupMappingProviderAssistService(
            ILlmEpisodeGroupMappingAssistService assistService,
            TmdbApi tmdbApi,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            LlmAssistTriggerPolicy triggerPolicy,
            ILogger<LlmEpisodeGroupMappingProviderAssistService> logger)
            : this(assistService, tmdbApi, libraryManager, providerManager, fileSystem, triggerPolicy, new EpisodeGroupRefreshService(), logger)
        {
        }

        public LlmEpisodeGroupMappingProviderAssistService(
            ILlmEpisodeGroupMappingAssistService assistService,
            TmdbApi tmdbApi,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            LlmAssistTriggerPolicy triggerPolicy,
            EpisodeGroupRefreshService refreshService,
            ILogger<LlmEpisodeGroupMappingProviderAssistService> logger)
        {
            this.assistService = assistService ?? throw new ArgumentNullException(nameof(assistService));
            this.tmdbApi = tmdbApi ?? throw new ArgumentNullException(nameof(tmdbApi));
            this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            this.providerManager = providerManager ?? throw new ArgumentNullException(nameof(providerManager));
            this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            this.triggerPolicy = triggerPolicy ?? throw new ArgumentNullException(nameof(triggerPolicy));
            this.refreshService = refreshService ?? throw new ArgumentNullException(nameof(refreshService));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<LlmEpisodeGroupMappingAssistResult> SuggestWriteAndRefreshAsync(LlmEpisodeGroupMappingProviderAssistRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var currentMapping = request.Configuration?.TmdbEpisodeGroupMap ?? string.Empty;
            var triggerDecision = this.triggerPolicy.Evaluate(new LlmAssistTriggerContext
            {
                Configuration = request.Configuration,
                Semantic = request.Semantic,
                MediaType = request.MediaType,
                IsImageProvider = false,
                HttpContext = request.HttpContext,
            });
            if (!triggerDecision.ShouldTrigger)
            {
                return LlmEpisodeGroupMappingAssistResult.NotTriggered(triggerDecision.Reason, currentMapping);
            }

            var candidates = await this.GetCandidateGroupsAsync(request.SeriesTmdbId, request.MetadataLanguage, cancellationToken).ConfigureAwait(false);
            var result = await this.assistService.SuggestAndWriteAsync(
                    new LlmEpisodeGroupMappingAssistRequest
                    {
                        Configuration = request.Configuration,
                        SeriesTmdbId = request.SeriesTmdbId,
                        SeriesTitle = request.SeriesTitle,
                        MetadataLanguage = request.MetadataLanguage,
                        SafeRelativePathSamples = request.SafeRelativePathSamples,
                        EpisodeDistribution = request.EpisodeDistribution,
                        CandidateGroups = candidates,
                        IsManualTrigger = true,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.WroteMapping)
            {
                this.QueueAffectedSeriesRefresh(currentMapping, result.MappingText);
            }

            return result;
        }

        private async Task<IReadOnlyList<LlmEpisodeGroupCandidate>> GetCandidateGroupsAsync(int? seriesTmdbId, string? metadataLanguage, CancellationToken cancellationToken)
        {
            if (!seriesTmdbId.HasValue || seriesTmdbId.Value <= 0)
            {
                return Array.Empty<LlmEpisodeGroupCandidate>();
            }

            var normalizedLanguage = metadataLanguage ?? string.Empty;
            var series = await this.tmdbApi.GetSeriesAsync(seriesTmdbId.Value, normalizedLanguage, normalizedLanguage, cancellationToken).ConfigureAwait(false);
            return series?.EpisodeGroups?.Results == null
                ? Array.Empty<LlmEpisodeGroupCandidate>()
                : series.EpisodeGroups.Results
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id))
                    .Select(CreateCandidate)
                    .ToArray();
        }

        private void QueueAffectedSeriesRefresh(string oldMapping, string newMapping)
        {
            var refreshResult = this.refreshService.CreateRefreshResult(oldMapping, newMapping);
            if (refreshResult.AffectedSeriesIds.Count == 0)
            {
                return;
            }

            var affectedSeriesIds = new HashSet<string>(refreshResult.AffectedSeriesIds, StringComparer.OrdinalIgnoreCase);
            var items = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
                HasTmdbId = true,
            });

            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(this.fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ReplaceAllImages = false,
            };

            var queued = 0;
            foreach (var item in items)
            {
                if (!item.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId)
                    || !affectedSeriesIds.Contains(tmdbId)
                    || item.Id == Guid.Empty)
                {
                    continue;
                }

                this.providerManager.QueueRefresh(item.Id, refreshOptions, RefreshPriority.High);
                queued++;
            }

            LogQueuedRefresh(this.logger, queued, null);
        }

        private static LlmEpisodeGroupCandidate CreateCandidate(TvGroupCollection candidate)
        {
            return new LlmEpisodeGroupCandidate
            {
                GroupId = candidate.Id,
                Name = candidate.Name,
                Type = candidate.Type.ToString(),
                GroupCount = candidate.GroupCount,
                EpisodeCount = candidate.EpisodeCount,
            };
        }
    }
}
