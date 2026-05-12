// <copyright file="LlmTmdbCorrectionMetadataPostProcessService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Entities;
    using Microsoft.Extensions.Logging;

    public sealed class LlmTmdbCorrectionMetadataPostProcessService : ILlmTmdbCorrectionMetadataPostProcessService
    {
        public const string ItemUpdatedTrigger = ILlmTmdbCorrectionMetadataPostProcessService.ItemUpdatedTrigger;

        private readonly ILlmTmdbCorrectionMetadataStore metadataStore;
        private readonly ILogger<LlmTmdbCorrectionMetadataPostProcessService> logger;

        public LlmTmdbCorrectionMetadataPostProcessService(ILlmTmdbCorrectionMetadataStore metadataStore, ILogger<LlmTmdbCorrectionMetadataPostProcessService> logger)
        {
            ArgumentNullException.ThrowIfNull(metadataStore);
            ArgumentNullException.ThrowIfNull(logger);

            this.metadataStore = metadataStore;
            this.logger = logger;
        }

        public async Task TryApplyAsync(ItemChangeEventArgs e, string triggerName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(e);
            ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(triggerName, ILlmTmdbCorrectionMetadataPostProcessService.ItemUpdatedTrigger, StringComparison.Ordinal))
            {
                throw new ArgumentOutOfRangeException(nameof(triggerName), triggerName, "Only ItemUpdated trigger is supported.");
            }

            if (e.Item is not BaseItem item || (item is not Movie && item is not Series))
            {
                return;
            }

            if (item.Id == Guid.Empty
                || (!e.UpdateReason.HasFlag(ItemUpdateType.MetadataImport)
                    && !e.UpdateReason.HasFlag(ItemUpdateType.MetadataDownload)))
            {
                return;
            }

            var claimToken = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var snapshot = this.metadataStore.TryClaim(item.Id, item.Path ?? string.Empty, item.Id, item.Path ?? string.Empty, claimToken);
            if (snapshot == null)
            {
                return;
            }

            if (item.IsLocked)
            {
                this.metadataStore.ReleaseClaim(snapshot.ItemId, snapshot.ItemPath, claimToken);
                return;
            }

            var changed = false;
            changed |= SetTextIfDifferent(item.Name, snapshot.Name, value => item.Name = value);
            changed |= SetTextIfDifferent(item.OriginalTitle, snapshot.OriginalTitle, value => item.OriginalTitle = value);
            changed |= SetTextIfDifferent(item.Overview, snapshot.Overview, value => item.Overview = value);
            changed |= SetIntIfDifferent(item.ProductionYear, snapshot.ProductionYear, value => item.ProductionYear = value);
            changed |= SetDateIfDifferent(item.PremiereDate, snapshot.PremiereDate, value => item.PremiereDate = value);
            changed |= SetProviderIdIfDifferent(item, MetadataProvider.Tmdb.ToString(), snapshot.TmdbId);
            changed |= SetProviderIdIfDifferent(item, MetaSharkPlugin.ProviderId, $"{MetaSource.Tmdb}_{snapshot.TmdbId}");
            changed |= CopyProviderIdIfPresent(item, snapshot.ProviderIds, MetadataProvider.Imdb.ToString());
            changed |= CopyProviderIdIfPresent(item, snapshot.ProviderIds, MetadataProvider.Tvdb.ToString());
            changed |= RemoveProviderIdIfPresent(item, Providers.BaseProvider.DoubanProviderId);

            if (!changed)
            {
                this.metadataStore.Remove(snapshot.ItemId, snapshot.ItemPath);
                return;
            }

            var updateReason = item.OnMetadataChanged();
            if (updateReason == ItemUpdateType.None)
            {
                updateReason = ItemUpdateType.MetadataEdit;
            }

            await item.UpdateToRepositoryAsync(updateReason, cancellationToken).ConfigureAwait(false);
            this.metadataStore.Remove(snapshot.ItemId, snapshot.ItemPath);
            this.logger.LogInformation(
                "[MetaShark] 已结清 LLM TMDb 纠错元数据. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} tmdbId={TmdbId}.",
                item.Id,
                triggerName,
                item.Path ?? string.Empty,
                snapshot.TmdbId);
        }

        private static bool SetTextIfDifferent(string? current, string? expected, Action<string?> setter)
        {
            if (string.Equals(current ?? string.Empty, expected ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }

            setter(expected);
            return true;
        }

        private static bool SetIntIfDifferent(int? current, int? expected, Action<int?> setter)
        {
            if (current == expected)
            {
                return false;
            }

            setter(expected);
            return true;
        }

        private static bool SetDateIfDifferent(DateTime? current, DateTime? expected, Action<DateTime?> setter)
        {
            if (current == expected)
            {
                return false;
            }

            setter(expected);
            return true;
        }

        private static bool SetProviderIdIfDifferent(BaseItem item, string providerIdKey, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var current = item.GetProviderId(providerIdKey);
            if (string.Equals(current ?? string.Empty, value, StringComparison.Ordinal))
            {
                return false;
            }

            item.SetProviderId(providerIdKey, value);
            return true;
        }

        private static bool CopyProviderIdIfPresent(BaseItem item, IReadOnlyDictionary<string, string>? providerIds, string providerIdKey)
        {
            if (providerIds == null || !providerIds.TryGetValue(providerIdKey, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return SetProviderIdIfDifferent(item, providerIdKey, value);
        }

        private static bool RemoveProviderIdIfPresent(BaseItem item, string providerIdKey)
        {
            if (item.ProviderIds == null || !item.ProviderIds.Remove(providerIdKey))
            {
                return false;
            }

            return true;
        }
    }
}
