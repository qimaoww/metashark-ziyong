// <copyright file="MovieSeriesPeopleRefreshStatePostProcessService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Core;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;
    using Microsoft.Extensions.Logging;

    public class MovieSeriesPeopleRefreshStatePostProcessService
    {
        public const string ItemUpdatedTrigger = "ItemUpdated";

        private const string LegacyPeopleRefreshStateProviderId = "MetaSharkPeopleRefreshState";

        private static readonly Regex LegacyPeopleRefreshStateNfoTagRegex = new Regex(
            @"^[ \t]*<metasharkpeoplerefreshstateid>.*?</metasharkpeoplerefreshstateid>[ \t]*\r?\n?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Action<ILogger, Guid, string, string, ItemUpdateType, string, Exception?> LogQueuedSearchMissingOverwriteRefresh =
            LoggerMessage.Define<Guid, string, string, ItemUpdateType, string>(LogLevel.Information, new EventId(4, nameof(TryQueueSearchMissingMetadataOverwriteRefresh)), "[MetaShark] 已排队影视人物 search-missing 单次 overwrite refresh. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} queuedItemPath={QueuedItemPath}.");

        private readonly ILogger<MovieSeriesPeopleRefreshStatePostProcessService> logger;
        private readonly IPeopleRefreshStateStore stateStore;
        private readonly IProviderManager? providerManager;
        private readonly IMovieSeriesPeopleOverwriteRefreshCandidateStore? overwriteRefreshCandidateStore;
        private readonly IFileSystem? fileSystem;

        public MovieSeriesPeopleRefreshStatePostProcessService(ILogger<MovieSeriesPeopleRefreshStatePostProcessService> logger, IPeopleRefreshStateStore stateStore, IProviderManager? providerManager = null, IMovieSeriesPeopleOverwriteRefreshCandidateStore? overwriteRefreshCandidateStore = null, IFileSystem? fileSystem = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(stateStore);

            this.logger = logger;
            this.stateStore = stateStore;
            this.providerManager = providerManager;
            this.overwriteRefreshCandidateStore = overwriteRefreshCandidateStore ?? InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore.Shared;
            this.fileSystem = fileSystem;
        }

#pragma warning disable CA1848
        public virtual async Task TryApplyAsync(ItemChangeEventArgs e, string triggerName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(e);
            ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
            cancellationToken.ThrowIfCancellationRequested();

            ValidateTriggerName(triggerName);

            if (e.Item is not BaseItem item)
            {
                return;
            }

            if (item.Id == Guid.Empty)
            {
                return;
            }

            this.logger.LogDebug(
                "[MetaShark] 收到影视人物刷新状态后处理事件. trigger={Trigger} itemId={ItemId} itemPath={ItemPath} updateReason={UpdateReason}.",
                triggerName,
                item.Id,
                item.Path ?? string.Empty,
                e.UpdateReason);

            if (!IsAcceptedUpdateReason(e.UpdateReason))
            {
                this.LogSkip("UpdateReasonRejected", triggerName, item, e.UpdateReason, null);
                return;
            }

            if (item is not Movie and not Series)
            {
                this.LogSkip("UnsupportedItemType", triggerName, item, e.UpdateReason, item.GetType().Name);
                return;
            }

            if (this.TryQueueSearchMissingMetadataOverwriteRefresh(item, triggerName, e.UpdateReason))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(item.GetProviderId(MetadataProvider.Tmdb)))
            {
                this.LogSkip("MissingTmdbProviderId", triggerName, item, e.UpdateReason, null);
                return;
            }

            var legacyResidueRemoved = RemoveLegacyPeopleRefreshStateProviderId(item);

            if (PeopleRefreshState.HasCurrentState(item, this.stateStore.GetState(item.Id)))
            {
                await this.PersistLegacyResidueCleanupAsync(item, triggerName, e.UpdateReason, legacyResidueRemoved, cancellationToken).ConfigureAwait(false);
                var legacyNfoResidueRemoved = this.CleanupLegacyPeopleRefreshStateNfoResidue(item, triggerName, e.UpdateReason);

                if (legacyResidueRemoved || legacyNfoResidueRemoved)
                {
                    return;
                }

                this.LogSkip("AlreadyCurrentState", triggerName, item, e.UpdateReason, PeopleRefreshState.CurrentVersion);
                return;
            }

            if (!PeopleRefreshState.TryCreateCurrent(item, out var nextState) || nextState == null)
            {
                this.LogSkip("StateSnapshotRejected", triggerName, item, e.UpdateReason, null);
                return;
            }

            try
            {
                this.stateStore.Save(nextState);
            }
            catch (Exception ex)
            {
                this.logger.LogError(
                    ex,
                    "[MetaShark] 影视人物刷新状态保存失败. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason}.",
                    item.Id,
                    triggerName,
                    item.Path ?? string.Empty,
                    e.UpdateReason);
                throw;
            }

            await this.PersistLegacyResidueCleanupAsync(item, triggerName, e.UpdateReason, legacyResidueRemoved, cancellationToken).ConfigureAwait(false);
            this.CleanupLegacyPeopleRefreshStateNfoResidue(item, triggerName, e.UpdateReason);

            this.logger.LogInformation(
                "[MetaShark] 已结清影视人物刷新状态. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} stateVersion={StateVersion}.",
                item.Id,
                triggerName,
                item.Path ?? string.Empty,
                e.UpdateReason,
                PeopleRefreshState.CurrentVersion);
        }
#pragma warning restore CA1848

        private static bool IsAcceptedUpdateReason(ItemUpdateType updateReason)
        {
            return updateReason.HasFlag(ItemUpdateType.MetadataImport)
                || updateReason.HasFlag(ItemUpdateType.MetadataDownload);
        }

        private static void ValidateTriggerName(string triggerName)
        {
            if (string.Equals(triggerName, ItemUpdatedTrigger, StringComparison.Ordinal))
            {
                return;
            }

            throw new ArgumentOutOfRangeException(nameof(triggerName), triggerName, "Only ItemUpdated trigger is supported.");
        }

        private static bool RemoveLegacyPeopleRefreshStateProviderId(BaseItem item)
        {
            return item.ProviderIds?.Remove(LegacyPeopleRefreshStateProviderId) ?? false;
        }

        private static MetadataRefreshOptions CreateSearchMissingOverwriteRefreshOptions(IFileSystem fileSystem)
        {
            return new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ReplaceAllImages = false,
            };
        }

        private static IEnumerable<string> GetLegacyPeopleRefreshStateNfoPaths(BaseItem item)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                yield break;
            }

            if (item is Series)
            {
                if (Directory.Exists(item.Path))
                {
                    yield return Path.Combine(item.Path, "tvshow.nfo");
                }

                yield break;
            }

            if (item is not Movie)
            {
                yield break;
            }

            if (Directory.Exists(item.Path))
            {
                yield return Path.Combine(item.Path, "movie.nfo");
                yield break;
            }

            var directory = Path.GetDirectoryName(item.Path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                yield break;
            }

            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(item.Path);
            if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            {
                yield return Path.Combine(directory, fileNameWithoutExtension + ".nfo");
            }

            yield return Path.Combine(directory, "movie.nfo");
        }

        private static int GetCurrentPeopleCount(BaseItem item)
        {
            var currentType = item.GetType();
            while (currentType != null)
            {
                var getPeopleMethod = currentType.GetMethod("GetPeople", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (getPeopleMethod?.Invoke(item, null) is IEnumerable peopleFromMethod)
                {
                    return CountEnumerable(peopleFromMethod);
                }

                var peopleProperty = currentType.GetProperty("People", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (peopleProperty?.GetValue(item) is IEnumerable peopleFromProperty)
                {
                    return CountEnumerable(peopleFromProperty);
                }

                currentType = currentType.BaseType;
            }

            return 0;
        }

        private static int CountEnumerable(IEnumerable values)
        {
            var count = 0;
            foreach (var value in values)
            {
                _ = value;
                count++;
            }

            return count;
        }

        private static bool TryRemoveLegacyPeopleRefreshStateTagFromNfo(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            var encoding = reader.CurrentEncoding;
            var updated = LegacyPeopleRefreshStateNfoTagRegex.Replace(content, string.Empty);
            if (string.Equals(content, updated, StringComparison.Ordinal))
            {
                return false;
            }

            File.WriteAllText(path, updated, encoding);
            return true;
        }

#pragma warning disable CA1848
        private async Task PersistLegacyResidueCleanupAsync(BaseItem item, string triggerName, ItemUpdateType updateReason, bool legacyResidueRemoved, CancellationToken cancellationToken)
        {
            if (!legacyResidueRemoved)
            {
                return;
            }

            try
            {
                var repositoryUpdateReason = item.OnMetadataChanged();
                if (repositoryUpdateReason == 0)
                {
                    repositoryUpdateReason = ItemUpdateType.MetadataEdit;
                }

                await item.UpdateToRepositoryAsync(repositoryUpdateReason, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(
                    ex,
                    "[MetaShark] 清理影视人物刷新 legacy provider id 残留失败. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} providerId={ProviderId}.",
                    item.Id,
                    triggerName,
                    item.Path ?? string.Empty,
                    updateReason,
                    LegacyPeopleRefreshStateProviderId);
                throw;
            }

            this.logger.LogInformation(
                "[MetaShark] 已清理影视人物刷新 legacy provider id 残留. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} providerId={ProviderId}.",
                item.Id,
                triggerName,
                item.Path ?? string.Empty,
                updateReason,
                LegacyPeopleRefreshStateProviderId);
        }

        private bool CleanupLegacyPeopleRefreshStateNfoResidue(BaseItem item, string triggerName, ItemUpdateType updateReason)
        {
            var cleaned = false;

            foreach (var nfoPath in GetLegacyPeopleRefreshStateNfoPaths(item))
            {
                try
                {
                    if (!TryRemoveLegacyPeopleRefreshStateTagFromNfo(nfoPath))
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogError(
                        ex,
                        "[MetaShark] 清理影视人物刷新 legacy NFO 残留失败. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} nfoPath={NfoPath}.",
                        item.Id,
                        triggerName,
                        item.Path ?? string.Empty,
                        updateReason,
                        nfoPath);
                    throw;
                }

                cleaned = true;
                this.logger.LogInformation(
                    "[MetaShark] 已清理影视人物刷新 legacy NFO 残留. itemId={ItemId} trigger={Trigger} itemPath={ItemPath} updateReason={UpdateReason} nfoPath={NfoPath}.",
                    item.Id,
                    triggerName,
                    item.Path ?? string.Empty,
                    updateReason,
                    nfoPath);
            }

            return cleaned;
        }
#pragma warning restore CA1848

        private bool TryQueueSearchMissingMetadataOverwriteRefresh(BaseItem item, string triggerName, ItemUpdateType updateReason)
        {
            if (!updateReason.HasFlag(ItemUpdateType.MetadataDownload)
                || this.providerManager == null
                || this.overwriteRefreshCandidateStore == null
                || this.fileSystem == null)
            {
                return false;
            }

            var candidate = this.overwriteRefreshCandidateStore.Consume(item.Id, item.Path ?? string.Empty);
            if (candidate == null)
            {
                return false;
            }

            var currentPeopleCount = GetCurrentPeopleCount(item);
            if (currentPeopleCount >= candidate.ExpectedPeopleCount)
            {
                return false;
            }

            if (candidate.OverwriteQueued)
            {
                this.overwriteRefreshCandidateStore.Save(candidate);
                return true;
            }

            try
            {
                this.providerManager.QueueRefresh(item.Id, CreateSearchMissingOverwriteRefreshOptions(this.fileSystem), RefreshPriority.Normal);
                candidate.OverwriteQueued = true;
                this.overwriteRefreshCandidateStore.Save(candidate);
            }
            catch
            {
                candidate.OverwriteQueued = false;
                this.overwriteRefreshCandidateStore.Save(candidate);
                throw;
            }

            LogQueuedSearchMissingOverwriteRefresh(
                this.logger,
                item.Id,
                triggerName,
                item.Path ?? string.Empty,
                updateReason,
                candidate.ItemPath ?? string.Empty,
                null);
            return true;
        }

#pragma warning disable CA1848
        private void LogSkip(string reason, string triggerName, BaseItem item, ItemUpdateType updateReason, string? detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                this.logger.LogInformation(
                    "[MetaShark] 跳过影视人物刷新状态结清. reason={Reason} trigger={Trigger} itemId={ItemId} itemPath={ItemPath} updateReason={UpdateReason}.",
                    reason,
                    triggerName,
                    item.Id,
                    item.Path ?? string.Empty,
                    updateReason);
                return;
            }

            this.logger.LogInformation(
                "[MetaShark] 跳过影视人物刷新状态结清. reason={Reason} trigger={Trigger} itemId={ItemId} itemPath={ItemPath} updateReason={UpdateReason} detail={Detail}.",
                reason,
                triggerName,
                item.Id,
                item.Path ?? string.Empty,
                updateReason,
                detail);
        }
#pragma warning restore CA1848
    }
}
