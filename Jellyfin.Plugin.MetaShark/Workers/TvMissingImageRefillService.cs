// <copyright file="TvMissingImageRefillService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.BaseItemManager;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Configuration;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;
    using Microsoft.Extensions.Logging;

    public sealed class TvMissingImageRefillService : ITvMissingImageRefillService
    {
        private static readonly Action<ILogger, int, int, int, string, Exception?> LogScanSummary =
            LoggerMessage.Define<int, int, int, string>(LogLevel.Information, new EventId(1, nameof(QueueMissingImagesForFullLibraryScan)), "[MetaShark] 电视缺图回填扫描完成. candidateCount={CandidateCount} queuedCount={QueuedCount} skippedCount={SkippedCount} skippedReasons={SkippedReasons}.");

        private static readonly Action<ILogger, string, Guid, string, Exception?> LogQueuedRefresh =
            LoggerMessage.Define<string, Guid, string>(LogLevel.Debug, new EventId(2, nameof(QueueMissingImagesForFullLibraryScan)), "[MetaShark] 已排队电视缺图回填. name={Name} itemId={Id} missingImages={MissingImages}.");

        private static readonly Action<ILogger, Guid, Exception?> LogSkipEmptyId =
            LoggerMessage.Define<Guid>(LogLevel.Debug, new EventId(3, nameof(QueueMissingImagesForUpdatedItem)), "[MetaShark] 跳过电视缺图回填. reason=EmptyId itemId={Id}.");

        private static readonly Action<ILogger, string, Exception?> LogSkipDisabledImageFetcher =
            LoggerMessage.Define<string>(LogLevel.Debug, new EventId(4, nameof(QueueMissingImagesForUpdatedItem)), "[MetaShark] 跳过电视缺图回填. reason=ImageFetcherDisabled name={Name}.");

        private static readonly Action<ILogger, string, Guid, Exception?> LogSkipNoMissingImages =
            LoggerMessage.Define<string, Guid>(LogLevel.Debug, new EventId(5, nameof(QueueIfMissingAndEnabled)), "[MetaShark] 跳过电视缺图回填. reason=NoMissingImages name={Name} itemId={Id}.");

        private static readonly Action<ILogger, string, Guid, ItemUpdateType, Exception?> LogSkipImageUpdate =
            LoggerMessage.Define<string, Guid, ItemUpdateType>(LogLevel.Debug, new EventId(6, nameof(QueueMissingImagesForUpdatedItem)), "[MetaShark] 跳过电视缺图回填. reason=UpdateReasonRejected name={Name} itemId={Id} updateReason={UpdateReason}.");

        private static readonly Action<ILogger, string, Guid, string, Exception?> LogSkipHardMiss =
            LoggerMessage.Define<string, Guid, string>(LogLevel.Debug, new EventId(7, nameof(QueueIfMissingAndEnabled)), "[MetaShark] 跳过电视缺图回填. state=HardMiss name={Name} itemId={Id} reason={Reason}.");

        private static readonly Action<ILogger, string, Guid, DateTimeOffset, Exception?> LogSkipCooldown =
            LoggerMessage.Define<string, Guid, DateTimeOffset>(LogLevel.Debug, new EventId(8, nameof(QueueIfMissingAndEnabled)), "[MetaShark] 跳过电视缺图回填. reason=CooldownActive name={Name} itemId={Id} nextRetryAtUtc={NextRetryAtUtc}.");

        private readonly ILogger<TvMissingImageRefillService> logger;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IBaseItemManager baseItemManager;
        private readonly IFileSystem fileSystem;
        private readonly ITvImageRefillStateStore retryStateStore;

        public TvMissingImageRefillService(
            ILoggerFactory loggerFactory,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IBaseItemManager baseItemManager,
            IFileSystem fileSystem,
            ITvImageRefillStateStore? retryStateStore = null)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);

            this.logger = loggerFactory.CreateLogger<TvMissingImageRefillService>();
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.baseItemManager = baseItemManager;
            this.fileSystem = fileSystem;
            this.retryStateStore = retryStateStore ?? new FileTvImageRefillStateStore(
                Path.Combine(Path.GetTempPath(), MetaSharkPlugin.PluginName, $"tv-image-refill-state-{Guid.NewGuid():N}.json"),
                loggerFactory);
        }

        public TvMissingImageRefillScanSummary QueueMissingImagesForFullLibraryScan(CancellationToken cancellationToken)
        {
            var items = this.GetTvItemsForRefill();

            var queuedCount = 0;
            var skippedCount = 0;
            var skippedReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var skippedReason = this.QueueIfMissingAndEnabled(item);
                if (skippedReason is null)
                {
                    queuedCount++;
                    continue;
                }

                skippedCount++;
                if (skippedReasonCounts.TryGetValue(skippedReason, out var count))
                {
                    skippedReasonCounts[skippedReason] = count + 1;
                }
                else
                {
                    skippedReasonCounts[skippedReason] = 1;
                }
            }

            var summary = new TvMissingImageRefillScanSummary(items.Count, queuedCount, skippedCount, FormatSkippedReasons(skippedReasonCounts));
            LogScanSummary(this.logger, summary.CandidateCount, summary.QueuedCount, summary.SkippedCount, summary.SkippedReasons, null);

            return summary;
        }

        public void QueueMissingImagesForUpdatedItem(ItemChangeEventArgs e, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(e);
            cancellationToken.ThrowIfCancellationRequested();

            var item = e.Item;
            if (e.UpdateReason == ItemUpdateType.ImageUpdate)
            {
                if (item != null && item.Id != Guid.Empty)
                {
                    this.retryStateStore.Remove(item.Id);
                }

                LogSkipImageUpdate(this.logger, item?.Name ?? string.Empty, item?.Id ?? Guid.Empty, e.UpdateReason, null);
                return;
            }

            this.QueueIfMissingAndEnabled(item);
        }

        private static string FormatSkippedReasons(Dictionary<string, int> skippedReasonCounts)
        {
            if (skippedReasonCounts.Count == 0)
            {
                return "None";
            }

            return string.Join(
                ";",
                skippedReasonCounts
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
        }

        private List<BaseItem> GetTvItemsForRefill()
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode },
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
            };

            return this.libraryManager.GetItemList(query);
        }

        private string? QueueIfMissingAndEnabled(BaseItem? item)
        {
            if (item is not Series && item is not Season && item is not Episode)
            {
                return "NonTvItem";
            }

            if (item.Id == Guid.Empty)
            {
                LogSkipEmptyId(this.logger, item.Id, null);
                return "EmptyId";
            }

            var fingerprint = TvImageRefillFingerprint.Create(item);
            var state = this.retryStateStore.GetState(item.Id);
            if (state != null && !string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                this.retryStateStore.Remove(item.Id);
                state = null;
            }

            if (this.TryHandleStructuralHardMiss(item, fingerprint, state))
            {
                return "HardMiss";
            }

            if (state?.Status == TvImageRefillStatus.HardMiss)
            {
                LogSkipHardMiss(this.logger, item.Name ?? string.Empty, item.Id, state.LastReason, null);
                return "HardMiss";
            }

            if (state?.Status == TvImageRefillStatus.CoolingDown && state.NextRetryAtUtc.HasValue && state.NextRetryAtUtc.Value > DateTimeOffset.UtcNow)
            {
                LogSkipCooldown(this.logger, item.Name ?? string.Empty, item.Id, state.NextRetryAtUtc.Value, null);
                return "CooldownActive";
            }

            var typeOptions = this.GetTypeOptions(item);
            if (!this.baseItemManager.IsImageFetcherEnabled(item, typeOptions, MetaSharkPlugin.PluginName))
            {
                LogSkipDisabledImageFetcher(this.logger, item.Name ?? item.Id.ToString(), null);
                return "ImageFetcherDisabled";
            }

            var missingImages = TvImageSupport.GetMissingImages(item).ToArray();
            if (missingImages.Length == 0)
            {
                this.retryStateStore.Remove(item.Id);
                LogSkipNoMissingImages(this.logger, item.Name ?? string.Empty, item.Id, null);
                return "NoMissingImages";
            }

            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(this.fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
            };

            this.retryStateStore.Save(new TvImageRefillState
            {
                ItemId = item.Id,
                Fingerprint = fingerprint,
                Status = TvImageRefillStatus.CoolingDown,
                AttemptCount = (state?.AttemptCount ?? 0) + 1,
                LastReason = "QueuedRefresh",
                NextRetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(30),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });

            LogQueuedRefresh(this.logger, item.Name ?? string.Empty, item.Id, string.Join(",", missingImages), null);
            this.providerManager.QueueRefresh(item.Id, refreshOptions, RefreshPriority.Normal);

            return null;
        }

        private bool TryHandleStructuralHardMiss(BaseItem item, string fingerprint, TvImageRefillState? currentState)
        {
            if (item is not Episode episode)
            {
                return false;
            }

            if (episode.IndexNumber is <= 0)
            {
                this.retryStateStore.Save(new TvImageRefillState
                {
                    ItemId = item.Id,
                    Fingerprint = fingerprint,
                    Status = TvImageRefillStatus.HardMiss,
                    AttemptCount = currentState?.AttemptCount ?? 0,
                    LastReason = "InvalidEpisodeNumber",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });
                return true;
            }

            return false;
        }

        private TypeOptions? GetTypeOptions(BaseItem item)
        {
            var libraryOptions = this.libraryManager.GetLibraryOptions(item);
            var typeOptions = libraryOptions?.TypeOptions;
            if (typeOptions == null)
            {
                return null;
            }

            var targetType = item switch
            {
                Series => nameof(Series),
                Season => nameof(Season),
                Episode => nameof(Episode),
                _ => string.Empty,
            };

            if (string.IsNullOrEmpty(targetType))
            {
                return null;
            }

            return typeOptions.FirstOrDefault(x => string.Equals(x.Type, targetType, StringComparison.OrdinalIgnoreCase));
        }
    }
}
