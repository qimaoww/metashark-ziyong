// <copyright file="LlmEpisodeGroupMappingProviderAssistService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Providers;
    using Jellyfin.Plugin.MetaShark.Providers.Llm;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Objects.TvShows;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before non-static members", Justification = "Keep orchestration flow before helper methods.")]
    public sealed class LlmEpisodeGroupMappingProviderAssistService : ILlmEpisodeGroupMappingProviderAssistService
    {
        private static readonly TimeSpan RefreshLoopGuardWindow = TimeSpan.FromSeconds(30);

        private static readonly Action<ILogger, int, Exception?> LogQueuedRefresh =
            LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, nameof(LlmEpisodeGroupMappingProviderAssistService)), "[MetaShark] LLM 剧集组映射变更已排队刷新. Count={Count}.");

        private static readonly Action<ILogger, string, string, string, string, int, bool, Exception?> LogAssistCompleted =
            LoggerMessage.Define<string, string, string, string, int, bool>(LogLevel.Information, new EventId(2, "EpisodeGroupMappingAssist.Completed"), "[MetaShark] LLM 剧集组映射辅助完成. status={Status} reason={ReasonCode} seriesTmdbId={SeriesTmdbId} selectedGroupId={SelectedGroupId} candidateCount={CandidateCount} wroteMapping={WroteMapping}.");

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> SeriesLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (string GroupId, DateTimeOffset ExpiresAt)> SuppressedRefreshSeriesIds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (string GroupId, DateTimeOffset ExpiresAt)> RecentlyQueuedRefreshSeriesIds = new(StringComparer.OrdinalIgnoreCase);

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
            var currentMapping = request.Configuration?.LlmTmdbEpisodeGroupMap ?? string.Empty;
            var seriesTmdbIdText = request.SeriesTmdbId.HasValue && request.SeriesTmdbId.Value > 0
                ? request.SeriesTmdbId.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

            if (ShouldSuppressRefreshLoop(request, seriesTmdbIdText))
            {
                return LlmEpisodeGroupMappingAssistResult.NoChange("RefreshLoopSuppressed", currentMapping, string.Empty);
            }

            var seriesLock = GetSeriesLock(seriesTmdbIdText);
            await seriesLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                currentMapping = request.Configuration?.LlmTmdbEpisodeGroupMap ?? string.Empty;
                var triggerDecision = this.triggerPolicy.Evaluate(new LlmAssistTriggerContext
                {
                    Configuration = request.Configuration,
                    Semantic = request.Semantic,
                    MediaType = request.MediaType,
                    IsImageProvider = false,
                    HttpContext = request.HttpContext,
                    AllowOverwriteRefresh = true,
                    HasBridgedExplicitOverwriteMetadataRefreshIntent = request.HasBridgedExplicitOverwriteMetadataRefreshIntent,
                });
                if (!triggerDecision.ShouldTrigger && IsBridgedExplicitSearchMissingRefresh(request, triggerDecision))
                {
                    triggerDecision = LlmAssistTriggerDecision.Allowed("ExplicitSearchMissingMetadataRefresh");
                }

                if (!triggerDecision.ShouldTrigger && IsBridgedExplicitOverwriteRefresh(request, triggerDecision))
                {
                    triggerDecision = LlmAssistTriggerDecision.Allowed("ExplicitOverwriteRefresh");
                }

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

                LogAssistResult(this.logger, result, seriesTmdbIdText, candidates.Count);
                if (result.WroteMapping)
                {
                    this.QueueAffectedSeriesRefresh(
                        TmdbEpisodeGroupMapping.GetEffectiveMappingText(request.Configuration?.TmdbEpisodeGroupMap, result.PreviousMapping),
                        TmdbEpisodeGroupMapping.GetEffectiveMappingText(request.Configuration?.TmdbEpisodeGroupMap, result.MappingText));
                }

                return result;
            }
            finally
            {
                seriesLock.Release();
                ReleaseSeriesLock(seriesTmdbIdText, seriesLock);
            }
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

        private static void LogAssistResult(ILogger logger, LlmEpisodeGroupMappingAssistResult result, string seriesTmdbId, int candidateCount)
        {
            ArgumentNullException.ThrowIfNull(result);

            LogAssistCompleted(
                logger,
                result.Status.ToString(),
                string.IsNullOrWhiteSpace(result.Reason) ? "Updated" : result.Reason,
                string.IsNullOrWhiteSpace(seriesTmdbId) ? "Unknown" : seriesTmdbId,
                string.IsNullOrWhiteSpace(result.SelectedGroupId) ? string.Empty : result.SelectedGroupId!,
                candidateCount,
                result.WroteMapping,
                null);
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

                var newGroupId = refreshResult.NewSnapshot.TryGetGroupId(tmdbId, out var resolvedGroupId)
                    ? resolvedGroupId
                    : string.Empty;

                if (IsRecentlyQueuedRefresh(tmdbId, newGroupId))
                {
                    continue;
                }

                MarkRefreshQueued(tmdbId, newGroupId);
                this.providerManager.QueueRefresh(item.Id, refreshOptions, RefreshPriority.High);
                queued++;
            }

            LogQueuedRefresh(this.logger, queued, null);
        }

        private static SemaphoreSlim GetSeriesLock(string seriesTmdbId)
        {
            return SeriesLocks.GetOrAdd(seriesTmdbId ?? string.Empty, _ => new SemaphoreSlim(1, 1));
        }

        private static void ReleaseSeriesLock(string seriesTmdbId, SemaphoreSlim seriesLock)
        {
            if (seriesLock.CurrentCount == 1)
            {
                SeriesLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(seriesTmdbId ?? string.Empty, seriesLock));
            }
        }

        private static bool IsBridgedExplicitSearchMissingRefresh(LlmEpisodeGroupMappingProviderAssistRequest request, LlmAssistTriggerDecision triggerDecision)
        {
            return request.HasBridgedExplicitSearchMissingMetadataRefreshIntent
                && request.Semantic == DefaultScraperSemantic.UserRefresh
                && string.Equals(triggerDecision.Reason, "ImplicitRefreshRejected", StringComparison.Ordinal);
        }

        private static bool IsBridgedExplicitOverwriteRefresh(LlmEpisodeGroupMappingProviderAssistRequest request, LlmAssistTriggerDecision triggerDecision)
        {
            return request.HasBridgedExplicitOverwriteMetadataRefreshIntent
                && triggerDecision.Reason is "ImplicitRefreshRejected" or "AutomaticRefreshRejected";
        }

        private static bool ShouldSuppressRefreshLoop(LlmEpisodeGroupMappingProviderAssistRequest request, string seriesTmdbId)
        {
            if (request.Semantic != DefaultScraperSemantic.UserRefresh || string.IsNullOrWhiteSpace(seriesTmdbId))
            {
                return false;
            }

            var currentMapping = TmdbEpisodeGroupMapping.GetEffectiveMappingText(
                request.Configuration?.TmdbEpisodeGroupMap,
                request.Configuration?.LlmTmdbEpisodeGroupMap);
            var currentSnapshot = EpisodeGroupMapParser.Shared.ParseSnapshot(currentMapping);
            var currentGroupId = currentSnapshot.TryGetGroupId(seriesTmdbId, out var resolvedGroupId)
                ? resolvedGroupId
                : string.Empty;

            return TryConsumeRefreshSuppression(seriesTmdbId, currentGroupId);
        }

        private static bool TryConsumeRefreshSuppression(string seriesTmdbId, string groupId)
        {
            if (!SuppressedRefreshSeriesIds.TryGetValue(seriesTmdbId, out var state))
            {
                return false;
            }

            if (state.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                SuppressedRefreshSeriesIds.TryRemove(seriesTmdbId, out _);
                return false;
            }

            if (!string.Equals(state.GroupId, groupId ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }

            SuppressedRefreshSeriesIds.TryRemove(seriesTmdbId, out _);
            return true;
        }

        private static bool IsRecentlyQueuedRefresh(string seriesTmdbId, string groupId)
        {
            if (string.IsNullOrWhiteSpace(seriesTmdbId))
            {
                return false;
            }

            if (!RecentlyQueuedRefreshSeriesIds.TryGetValue(seriesTmdbId, out var state))
            {
                return false;
            }

            if (state.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                RecentlyQueuedRefreshSeriesIds.TryRemove(seriesTmdbId, out _);
                return false;
            }

            return string.Equals(state.GroupId, groupId ?? string.Empty, StringComparison.Ordinal);
        }

        private static void MarkRefreshQueued(string seriesTmdbId, string groupId)
        {
            if (string.IsNullOrWhiteSpace(seriesTmdbId))
            {
                return;
            }

            var expiresAt = DateTimeOffset.UtcNow.Add(RefreshLoopGuardWindow);
            var state = (groupId ?? string.Empty, expiresAt);
            RecentlyQueuedRefreshSeriesIds[seriesTmdbId] = state;
            SuppressedRefreshSeriesIds[seriesTmdbId] = state;
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
